using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using PaymentGateway.Api.Bank;

using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;
using PaymentGateway.Api.Validation;

using Shouldly;

namespace PaymentGateway.Api.Tests.Unit.Services;

public class PaymentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPaymentsRepository _repository = new();
    private readonly IAcquiringBankClient _bank = Substitute.For<IAcquiringBankClient>();
    private readonly CapturingLogger<PaymentService> _logger = new();
    private readonly RequestFingerprinter _fingerprinter = new("test-secret");
    private readonly PaymentService _service;

    public PaymentServiceTests()
    {
        var timeProvider = new FakeTimeProvider(Now);
        _service = new PaymentService(
            _repository, new PostPaymentRequestValidator(timeProvider), _fingerprinter, _bank, timeProvider, _logger);
    }

    [Fact]
    public async Task ProcessAsync_InvalidRequest_ReturnsRejectedWithValidationErrors()
    {
        var idempotencyKey = Guid.NewGuid();
        var result = await _service.ProcessAsync(idempotencyKey, ExpiredRequest());

        var rejected = result.ShouldBeOfType<PaymentResult.Rejected>();
        rejected.Errors.Keys.ShouldContain("ExpiryDate");
        (await _repository.GetByIdempotencyKeyAsync(idempotencyKey)).ShouldBeNull();
        await _bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
        var log = _logger.Entries.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Information);
        log.Message.ShouldStartWith("Payment request rejected by validation");
        log.Properties["ValidationErrorCount"].ShouldBe(1);
        log.Properties.ContainsKey("ElapsedMilliseconds").ShouldBeTrue();
        log.Properties.ContainsKey("PaymentId").ShouldBeFalse();
        log.Properties.ContainsKey("RequestFingerprint").ShouldBeFalse();
        log.Message.ShouldNotContain("2222405343248877");
        log.Message.ShouldNotContain("7391");
        log.Properties.Values.Select(value => value?.ToString())
            .ShouldNotContain(_fingerprinter.Compute(ExpiredRequest()));
    }

    [Fact]
    public async Task ProcessAsync_InvalidRequest_DoesNotStorePayment()
    {
        var idempotencyKey = Guid.NewGuid();
        var request = ExpiredRequest();

        var result = await _service.ProcessAsync(idempotencyKey, request);

        result.ShouldBeOfType<PaymentResult.Rejected>();
        (await _repository.GetByIdempotencyKeyAsync(idempotencyKey)).ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAsync_BankAuthorizes_StoresAndReturnsAuthorizedPayment()
    {
        var idempotencyKey = Guid.NewGuid();
        var request = ValidRequest();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(true, "auth-code"));

        var result = await _service.ProcessAsync(idempotencyKey, request);

        var payment = result.ShouldBeOfType<PaymentResult.Processed>().Payment;
        payment.Status.ShouldBe(PaymentStatus.Authorized);
        AssertOutcomeLog(PaymentStatus.Authorized, payment.Id);
        payment.AuthorizationCode.ShouldBe("auth-code");
        payment.IdempotencyKey.ShouldBe(idempotencyKey);
        payment.CardNumberLastFour.ShouldBe("8877");
        payment.ExpiryMonth.ShouldBe(4);
        payment.ExpiryYear.ShouldBe(2030);
        payment.Currency.ShouldBe("GBP");
        payment.Amount.ShouldBe(100);
        payment.CreatedAt.ShouldBe(Now);
        (await _repository.GetByIdAsync(payment.Id)).ShouldBe(payment);

        await _bank.Received(1).AuthorizeAsync(
            Arg.Is<BankPaymentRequest>(r =>
                r.CardNumber == "2222405343248877" && r.ExpiryMonth == 4 && r.ExpiryYear == 2030 &&
                r.Currency == "GBP" && r.Amount == 100 && r.Cvv == "7391"),
            Arg.Any<CancellationToken>());

        var serialised = JsonSerializer.Serialize(payment with { RequestFingerprint = "" });
        serialised.ShouldNotContain(request.CardNumber!);
        serialised.ShouldNotContain(request.Cvv!);
    }

    [Fact]
    public async Task ProcessAsync_BankDeclines_StoresAndReturnsDeclinedPayment()
    {
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(false, ""));

        var result = await _service.ProcessAsync(Guid.NewGuid(), ValidRequest());

        var payment = result.ShouldBeOfType<PaymentResult.Processed>().Payment;
        payment.Status.ShouldBe(PaymentStatus.Declined);
        AssertOutcomeLog(PaymentStatus.Declined, payment.Id);
        payment.AuthorizationCode.ShouldBeNull();
        payment.CardNumberLastFour.ShouldBe("8877");
        (await _repository.GetByIdAsync(payment.Id)).ShouldBe(payment);
    }

    [Fact]
    public async Task ProcessAsync_BankUnavailable_StoresFailedPaymentAndReturnsBankUnavailable()
    {
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AcquiringBankUnavailableException("down"));

        var result = await _service.ProcessAsync(Guid.NewGuid(), ValidRequest());

        var paymentId = result.ShouldBeOfType<PaymentResult.BankUnavailable>().PaymentId;
        var stored = await _repository.GetByIdAsync(paymentId);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(PaymentStatus.Failed);
        stored.AuthorizationCode.ShouldBeNull();
        stored.CardNumberLastFour.ShouldBe("8877");
        var log = _logger.Entries.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Warning);
        log.Message.ShouldStartWith("Acquiring bank unavailable; payment marked Failed");
        log.Properties["PaymentId"].ShouldBe(paymentId);
        log.Properties["Outcome"]?.ToString().ShouldBe(nameof(PaymentStatus.Failed));
        log.Properties.ContainsKey("ElapsedMilliseconds").ShouldBeTrue();
        log.Exception.ShouldBeOfType<AcquiringBankUnavailableException>();
    }

    [Fact]
    public async Task ProcessAsync_CallerCancelsDuringBankCall_StoresFailedPaymentAndPropagatesCancellation()
    {
        // A real store honours the caller's token, so model that to prove the Failed update still happens.
        var repository = new CancellationHonouringRepository(_repository);
        var timeProvider = new FakeTimeProvider(Now);
        var service = new PaymentService(
            repository, new PostPaymentRequestValidator(timeProvider), _fingerprinter, _bank, timeProvider, _logger);
        var idempotencyKey = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<BankPaymentResponse>>(_ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await Should.ThrowAsync<OperationCanceledException>(
            () => service.ProcessAsync(idempotencyKey, ValidRequest(), cancellation.Token));

        await AssertInterruptedPaymentStoredAsFailedAsync<OperationCanceledException>(service, idempotencyKey);
    }

    [Fact]
    public async Task ProcessAsync_UnexpectedExceptionDuringBankCall_StoresFailedPaymentAndRethrows()
    {
        var idempotencyKey = Guid.NewGuid();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("unexpected"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.ProcessAsync(idempotencyKey, ValidRequest()));

        await AssertInterruptedPaymentStoredAsFailedAsync<InvalidOperationException>(_service, idempotencyKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessAsync_SameKeyAndBodyAgain_ReplaysTheStoredPaymentWithoutCallingTheBankAgain(bool authorized)
    {
        var idempotencyKey = Guid.NewGuid();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(authorized, authorized ? "auth-code" : ""));
        var first = await _service.ProcessAsync(idempotencyKey, ValidRequest());

        var second = await _service.ProcessAsync(idempotencyKey, ValidRequest());

        second.ShouldBe(first);
        var firstPayment = first.ShouldBeOfType<PaymentResult.Processed>().Payment;
        var replayLog = _logger.Entries.Single(entry => entry.Message.StartsWith("Idempotent payment request replayed"));
        replayLog.Properties["PaymentId"].ShouldBe(firstPayment.Id);
        replayLog.Properties["Outcome"]?.ToString().ShouldBe(firstPayment.Status.ToString());
        replayLog.Properties.ContainsKey("ElapsedMilliseconds").ShouldBeTrue();
        await _bank.ReceivedWithAnyArgs(1).AuthorizeAsync(default!);
    }

    [Fact]
    public async Task ProcessAsync_SameKeyAndBodyWithDifferentCvv_StillReplays()
    {
        var idempotencyKey = Guid.NewGuid();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(true, "auth-code"));
        var first = await _service.ProcessAsync(idempotencyKey, ValidRequest());

        var second = await _service.ProcessAsync(idempotencyKey, ValidRequest(cvv: "0000"));

        second.ShouldBe(first);
        await _bank.ReceivedWithAnyArgs(1).AuthorizeAsync(default!);
    }

    [Fact]
    public async Task ProcessAsync_SameKeyAndRejectedBodyAgain_ReturnsValidationErrorsWithoutStoring()
    {
        var idempotencyKey = Guid.NewGuid();
        var first = await _service.ProcessAsync(idempotencyKey, ExpiredRequest());

        var second = await _service.ProcessAsync(idempotencyKey, ExpiredRequest());

        first.ShouldBeOfType<PaymentResult.Rejected>().Errors["ExpiryDate"]
            .ShouldBe(["Card expiry date must be in the future."]);
        second.ShouldBeOfType<PaymentResult.Rejected>().Errors["ExpiryDate"]
            .ShouldBe(["Card expiry date must be in the future."]);
        (await _repository.GetByIdempotencyKeyAsync(idempotencyKey)).ShouldBeNull();
        await _bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
    }

    [Fact]
    public async Task ProcessAsync_ValidRequestAfterRejectedRequestWithSameKey_ProcessesPayment()
    {
        var idempotencyKey = Guid.NewGuid();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(true, "auth-code"));

        var rejected = await _service.ProcessAsync(idempotencyKey, ExpiredRequest());
        var processed = await _service.ProcessAsync(idempotencyKey, ValidRequest());

        rejected.ShouldBeOfType<PaymentResult.Rejected>();
        processed.ShouldBeOfType<PaymentResult.Processed>().Payment.Status.ShouldBe(PaymentStatus.Authorized);
        (await _repository.GetByIdempotencyKeyAsync(idempotencyKey))?.Status.ShouldBe(PaymentStatus.Authorized);
        await _bank.Received(1).AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SameKeyAndBodyAfterBankFailure_ReplaysBankUnavailableWithoutRetrying()
    {
        var idempotencyKey = Guid.NewGuid();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AcquiringBankUnavailableException("down"));
        var first = await _service.ProcessAsync(idempotencyKey, ValidRequest());

        var second = await _service.ProcessAsync(idempotencyKey, ValidRequest());

        second.ShouldBe(first);
        await _bank.ReceivedWithAnyArgs(1).AuthorizeAsync(default!);
    }

    [Fact]
    public async Task ProcessAsync_SameKeyWithDifferentBody_ReturnsIdempotencyConflictWithoutCallingTheBankAgain()
    {
        var idempotencyKey = Guid.NewGuid();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(true, "auth-code"));
        await _service.ProcessAsync(idempotencyKey, ValidRequest());

        var second = await _service.ProcessAsync(idempotencyKey, ValidRequest(amount: 999));

        second.ShouldBeOfType<PaymentResult.IdempotencyConflict>();
        var conflictLog = _logger.Entries.Single(entry =>
            entry.Message.StartsWith("Idempotency key reused with a different payment request"));
        conflictLog.Level.ShouldBe(LogLevel.Warning);
        conflictLog.Message.ShouldStartWith("Idempotency key reused with a different payment request");
        conflictLog.Properties["PaymentId"].ShouldBe(
            (await _repository.GetByIdempotencyKeyAsync(idempotencyKey))!.Id);
        conflictLog.Properties.ContainsKey("ElapsedMilliseconds").ShouldBeTrue();
        await _bank.ReceivedWithAnyArgs(1).AuthorizeAsync(default!);
    }

    [Fact]
    public async Task ProcessAsync_SameKeyAndBodyWhileFirstIsStillPending_ReturnsInProgressWithoutCallingTheBank()
    {
        var idempotencyKey = Guid.NewGuid();
        var request = ValidRequest();
        await _repository.TryAddAsync(new Payment
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = _fingerprinter.Compute(request),
            Status = PaymentStatus.Pending,
            CreatedAt = Now,
            UpdatedAt = Now
        });

        var result = await _service.ProcessAsync(idempotencyKey, request);

        result.ShouldBeOfType<PaymentResult.InProgress>();
        var inProgressLog = _logger.Entries.ShouldHaveSingleItem();
        inProgressLog.Level.ShouldBe(LogLevel.Information);
        inProgressLog.Message.ShouldStartWith("Payment request is already in progress");
        inProgressLog.Properties["PaymentId"].ShouldBe(
            (await _repository.GetByIdempotencyKeyAsync(idempotencyKey))!.Id);
        inProgressLog.Properties.ContainsKey("ElapsedMilliseconds").ShouldBeTrue();
        await _bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
    }

    [Fact]
    public async Task ProcessAsync_ConcurrentRequestsWithTheSameKey_CallTheBankOnce()
    {
        var idempotencyKey = Guid.NewGuid();
        _bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(100);
                return new BankPaymentResponse(true, "auth-code");
            });

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _service.ProcessAsync(idempotencyKey, ValidRequest()))));

        await _bank.ReceivedWithAnyArgs(1).AuthorizeAsync(default!);
        results.ShouldAllBe(r => r is PaymentResult.Processed || r is PaymentResult.InProgress);
        results.OfType<PaymentResult.Processed>().Select(r => r.Payment.Id).Distinct().Count().ShouldBe(1);
    }

    private static PostPaymentRequest ExpiredRequest(string? cardNumber = "2222405343248877") =>
        ValidRequest(cardNumber, expiryYear: 2020);

    private static PostPaymentRequest ValidRequest(
        string? cardNumber = "2222405343248877", int expiryYear = 2030, int amount = 100, string cvv = "7391") => new()
    {
        CardNumber = cardNumber,
        ExpiryMonth = 4,
        ExpiryYear = expiryYear,
        Currency = "GBP",
        Amount = amount,
        Cvv = cvv
    };

    private void AssertOutcomeLog(PaymentStatus status, Guid paymentId)
    {
        var log = _logger.Entries.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Information);
        log.Message.ShouldStartWith("Payment completed with outcome " + status);
        log.Properties["PaymentId"].ShouldBe(paymentId);
        log.Properties["Outcome"]?.ToString().ShouldBe(status.ToString());
        log.Properties.ContainsKey("ElapsedMilliseconds").ShouldBeTrue();
        log.Properties.ContainsKey("RequestFingerprint").ShouldBeFalse();
        log.Message.ShouldNotContain("2222405343248877");
        log.Message.ShouldNotContain("7391");
        log.Properties.Values.Select(value => value?.ToString())
            .ShouldNotContain(_fingerprinter.Compute(ValidRequest()));
    }

    // The payment must not stay Pending: it is stored as Failed, logged, and replays as BankUnavailable.
    private async Task AssertInterruptedPaymentStoredAsFailedAsync<TException>(PaymentService service, Guid idempotencyKey)
        where TException : Exception
    {
        var stored = await _repository.GetByIdempotencyKeyAsync(idempotencyKey);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(PaymentStatus.Failed);
        stored.CardNumberLastFour.ShouldBe("8877");

        var log = _logger.Entries.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Warning);
        log.Message.ShouldStartWith("Payment processing interrupted; payment marked Failed");
        log.Properties["PaymentId"].ShouldBe(stored.Id);
        log.Properties.ContainsKey("ElapsedMilliseconds").ShouldBeTrue();
        log.Exception.ShouldBeOfType<TException>();

        var replay = await service.ProcessAsync(idempotencyKey, ValidRequest());
        replay.ShouldBe(new PaymentResult.BankUnavailable(stored.Id));
        await _bank.ReceivedWithAnyArgs(1).AuthorizeAsync(default!);
    }

    private sealed class CancellationHonouringRepository(IPaymentsRepository inner) : IPaymentsRepository
    {
        public Task<bool> TryAddAsync(Payment payment, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.TryAddAsync(payment, cancellationToken);
        }

        public Task<Payment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.GetByIdAsync(id, cancellationToken);
        }

        public Task<Payment?> GetByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        }

        public Task UpdateAsync(Payment payment, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.UpdateAsync(payment, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            _entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception), properties, exception));
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception);
}
