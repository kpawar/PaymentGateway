using System.Collections.ObjectModel;
using System.Diagnostics;

using FluentValidation;
using Microsoft.Extensions.Logging;

using PaymentGateway.Api.Bank;
using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Services;

public class PaymentService(
    IPaymentsRepository repository,
    IValidator<PostPaymentRequest> validator,
    RequestFingerprinter fingerprinter,
    IAcquiringBankClient bank,
    TimeProvider timeProvider,
    ILogger<PaymentService> logger) : IPaymentService
{
    public async Task<PaymentResult> ProcessAsync(
        Guid idempotencyKey, PostPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var fingerprint = fingerprinter.Compute(request);

        var existing = await repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return ResolveExisting(existing, fingerprint, startedAt);
        }

        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            var errors = new ReadOnlyDictionary<string, string[]>(validation.ToDictionary());
            logger.LogInformation(
                "Payment request rejected by validation with {ValidationErrorCount} errors after {ElapsedMilliseconds} ms",
                errors.Count,
                ElapsedMilliseconds(startedAt));

            return new PaymentResult.Rejected(errors);
        }

        var now = timeProvider.GetUtcNow();

        // Reserving the idempotency key is atomic, so concurrent duplicates cannot both reach the bank.
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = fingerprint,
            Status = PaymentStatus.Pending,
            CardNumberLastFour = LastFourDigits(request.CardNumber),
            ExpiryMonth = request.ExpiryMonth,
            ExpiryYear = request.ExpiryYear,
            Currency = request.Currency,
            Amount = request.Amount,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (!await repository.TryAddAsync(payment, cancellationToken))
        {
            existing = await repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException("The idempotency key was reserved but could not be retrieved.");

            return ResolveExisting(existing, fingerprint, startedAt);
        }

        BankPaymentResponse response;
        try
        {
            response = await bank.AuthorizeAsync(ToBankRequest(request), cancellationToken);
        }
        catch (AcquiringBankUnavailableException exception)
        {
            await CompleteAsync(payment with { Status = PaymentStatus.Failed }, cancellationToken);
            logger.LogWarning(
                exception,
                "Acquiring bank unavailable; payment marked {Outcome} after {ElapsedMilliseconds} ms for payment {PaymentId}",
                PaymentStatus.Failed,
                ElapsedMilliseconds(startedAt),
                payment.Id);

            return new PaymentResult.BankUnavailable(payment.Id);
        }
        catch (Exception exception)
        {
            // Caller cancellation or an unexpected error must not leave the key Pending forever.
            // The caller may already have gone, so the update deliberately ignores its token.
            await CompleteAsync(payment with { Status = PaymentStatus.Failed }, CancellationToken.None);
            logger.LogWarning(
                exception,
                "Payment processing interrupted; payment marked {Outcome} after {ElapsedMilliseconds} ms for payment {PaymentId}",
                PaymentStatus.Failed,
                ElapsedMilliseconds(startedAt),
                payment.Id);

            throw;
        }

        payment = response.Authorized
            ? payment with { Status = PaymentStatus.Authorized, AuthorizationCode = response.AuthorizationCode }
            : payment with { Status = PaymentStatus.Declined };
        await CompleteAsync(payment, cancellationToken);
        logger.LogInformation(
            "Payment completed with outcome {Outcome} for payment {PaymentId} after {ElapsedMilliseconds} ms",
            payment.Status,
            payment.Id,
            ElapsedMilliseconds(startedAt));

        return new PaymentResult.Processed(payment);
    }

    private async Task CompleteAsync(Payment payment, CancellationToken cancellationToken) =>
        await repository.UpdateAsync(payment with { UpdatedAt = timeProvider.GetUtcNow() }, cancellationToken);

    private static PaymentResult Replay(Payment payment) => payment.Status switch
    {
        PaymentStatus.Authorized or PaymentStatus.Declined => new PaymentResult.Processed(payment),
        PaymentStatus.Failed => new PaymentResult.BankUnavailable(payment.Id),
        PaymentStatus.Pending => new PaymentResult.InProgress(),
        _ => throw new ArgumentOutOfRangeException(nameof(payment), payment.Status, "Unknown payment status.")
    };

    private PaymentResult ResolveExisting(Payment payment, string fingerprint, long startedAt)
    {
        if (payment.RequestFingerprint != fingerprint)
        {
            logger.LogWarning(
                "Idempotency key reused with a different payment request for payment {PaymentId} after {ElapsedMilliseconds} ms",
                payment.Id,
                ElapsedMilliseconds(startedAt));
            return new PaymentResult.IdempotencyConflict();
        }

        if (payment.Status == PaymentStatus.Pending)
        {
            logger.LogInformation(
                "Payment request is already in progress for payment {PaymentId} after {ElapsedMilliseconds} ms",
                payment.Id,
                ElapsedMilliseconds(startedAt));
            return new PaymentResult.InProgress();
        }

        logger.LogDebug(
            "Idempotent payment request replayed with outcome {Outcome} for payment {PaymentId} after {ElapsedMilliseconds} ms",
            payment.Status,
            payment.Id,
            ElapsedMilliseconds(startedAt));
        return Replay(payment);
    }

    private static double ElapsedMilliseconds(long startedAt) =>
        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    private static BankPaymentRequest ToBankRequest(PostPaymentRequest request) => new()
    {
        CardNumber = request.CardNumber!,
        ExpiryMonth = request.ExpiryMonth!.Value,
        ExpiryYear = request.ExpiryYear!.Value,
        Currency = request.Currency!,
        Amount = request.Amount!.Value,
        Cvv = request.Cvv!
    };

    // Only a card number that is all digits and at least 4 long yields a trustworthy last four.
    private static string? LastFourDigits(string? cardNumber) =>
        cardNumber is { Length: >= 4 } && cardNumber.All(char.IsAsciiDigit) ? cardNumber[^4..] : null;
}
