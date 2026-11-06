using FluentValidation;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Validation;

public class PostPaymentRequestValidator : AbstractValidator<PostPaymentRequest>
{
    public PostPaymentRequestValidator()
    {
        // Messages deliberately never include the attempted value: card data must not leak into responses or logs.
        RuleFor(r => r.CardNumber)
            .NotEmpty().WithMessage("Card number is required.")
            .Length(14, 19).WithMessage("Card number must be between 14 and 19 characters long.")
            .Must(IsAllDigits).WithMessage("Card number must contain only digits.");

        RuleFor(r => r.ExpiryMonth)
            .NotNull().WithMessage("Expiry month is required.")
            .InclusiveBetween(1, 12).WithMessage("Expiry month must be between 1 and 12.");
    }

    private static bool IsAllDigits(string? value) => value is not null && value.All(char.IsAsciiDigit);
}
