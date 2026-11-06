using System.Text.Json;

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
    private readonly RequestFingerprinter _fingerprinter = new("test-secret");
    private readonly PaymentService _service;

    public PaymentServiceTests()
    {
        var timeProvider = new FakeTimeProvider(Now);
        _service = new PaymentService(
            _repository, new PostPaymentRequestValidator(timeProvider), _fingerprinter, _bank, timeProvider);
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
}
