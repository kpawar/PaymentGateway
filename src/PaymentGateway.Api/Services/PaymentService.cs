using System.Collections.ObjectModel;

using FluentValidation;

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
    TimeProvider timeProvider)
{
    public async Task<PaymentResult> ProcessAsync(
        Guid idempotencyKey, PostPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var fingerprint = fingerprinter.Compute(request);
        var now = timeProvider.GetUtcNow();

        // Reserving the idempotency key is atomic, so concurrent duplicates cannot both reach the bank.
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = fingerprint,
            Status = PaymentStatus.Pending,
            CardNumberLastFour = LastFourDigits(request.CardNumber),
            CreatedAt = now,
            UpdatedAt = now
        };
        if (!await repository.TryAddAsync(payment, cancellationToken))
        {
            var existing = (await repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken))!;

            return existing.RequestFingerprint == fingerprint ? Replay(existing) : new PaymentResult.IdempotencyConflict();
        }

        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            var errors = new ReadOnlyDictionary<string, string[]>(validation.ToDictionary());
            await CompleteAsync(payment with { Status = PaymentStatus.Rejected, RejectionReasons = errors }, cancellationToken);

            return new PaymentResult.Rejected(payment.Id, errors);
        }

        payment = payment with
        {
            ExpiryMonth = request.ExpiryMonth,
            ExpiryYear = request.ExpiryYear,
            Currency = request.Currency,
            Amount = request.Amount
        };

        BankPaymentResponse response;
        try
        {
            response = await bank.AuthorizeAsync(ToBankRequest(request), cancellationToken);
        }
        catch (AcquiringBankUnavailableException)
        {
            await CompleteAsync(payment with { Status = PaymentStatus.Failed }, cancellationToken);

            return new PaymentResult.BankUnavailable(payment.Id);
        }

        payment = response.Authorized
            ? payment with { Status = PaymentStatus.Authorized, AuthorizationCode = response.AuthorizationCode }
            : payment with { Status = PaymentStatus.Declined };
        await CompleteAsync(payment, cancellationToken);

        return new PaymentResult.Processed(payment);
    }

    private async Task CompleteAsync(Payment payment, CancellationToken cancellationToken) =>
        await repository.UpdateAsync(payment with { UpdatedAt = timeProvider.GetUtcNow() }, cancellationToken);

    private static PaymentResult Replay(Payment payment) => payment.Status switch
    {
        PaymentStatus.Authorized or PaymentStatus.Declined => new PaymentResult.Processed(payment),
        PaymentStatus.Rejected => new PaymentResult.Rejected(payment.Id, payment.RejectionReasons!),
        PaymentStatus.Failed => new PaymentResult.BankUnavailable(payment.Id),
        PaymentStatus.Pending => new PaymentResult.InProgress(),
        _ => throw new ArgumentOutOfRangeException(nameof(payment), payment.Status, "Unknown payment status.")
    };

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
