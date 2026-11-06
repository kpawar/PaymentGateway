using System.Collections.ObjectModel;

using FluentValidation;

using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Services;

public class PaymentService(
    IPaymentsRepository repository,
    IValidator<PostPaymentRequest> validator,
    RequestFingerprinter fingerprinter,
    TimeProvider timeProvider)
{
    public async Task<PaymentResult> ProcessAsync(
        Guid idempotencyKey, PostPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = fingerprinter.Compute(request),
            Status = PaymentStatus.Rejected,
            CardNumberLastFour = LastFourDigits(request.CardNumber),
            RejectionReasons = validation.Errors.Select(e => e.ErrorMessage).ToList(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.TryAddAsync(payment, cancellationToken);

        return new PaymentResult.Rejected(payment.Id, new ReadOnlyDictionary<string, string[]>(validation.ToDictionary()));
    }

    // Only a card number that is all digits and at least 4 long yields a trustworthy last four.
    private static string? LastFourDigits(string? cardNumber) =>
        cardNumber is { Length: >= 4 } && cardNumber.All(char.IsAsciiDigit) ? cardNumber[^4..] : null;
}
