using FluentValidation;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Validation;

public class PostPaymentRequestValidator : AbstractValidator<PostPaymentRequest>
{
    private static readonly string[] SupportedCurrencies = ["GBP", "USD", "EUR"];

    public PostPaymentRequestValidator(TimeProvider timeProvider)
    {
        // Messages deliberately never include the attempted value: card data must not leak into responses or logs.
        RuleFor(r => r.CardNumber)
            .NotEmpty().WithMessage("Card number is required.")
            .Length(14, 19).WithMessage("Card number must be between 14 and 19 characters long.")
            .Must(IsAllDigits).WithMessage("Card number must contain only digits.");

        RuleFor(r => r.ExpiryMonth)
            .NotNull().WithMessage("Expiry month is required.")
            .InclusiveBetween(1, 12).WithMessage("Expiry month must be between 1 and 12.");

        RuleFor(r => r.ExpiryYear)
            .NotNull().WithMessage("Expiry year is required.");

        // A card is valid through the end of its expiry month. Reported on the combined ExpiryDate field,
        // and only checked once the month and year are individually present and valid.
        RuleFor(r => r)
            .Must(r => !IsInPast(r.ExpiryYear!.Value, r.ExpiryMonth!.Value, timeProvider.GetUtcNow()))
            .When(r => r.ExpiryYear is not null && r.ExpiryMonth is >= 1 and <= 12)
            .OverridePropertyName("ExpiryDate")
            .WithMessage("Card expiry date must be in the future.");

        RuleFor(r => r.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Must(c => SupportedCurrencies.Contains(c)).WithMessage("Currency must be one of: GBP, USD, EUR.");
    }

    private static bool IsInPast(int year, int month, DateTimeOffset now) =>
        (year, month).CompareTo((now.Year, now.Month)) < 0;

    private static bool IsAllDigits(string? value) => value is not null && value.All(char.IsAsciiDigit);
}
