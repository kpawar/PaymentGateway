using Microsoft.Extensions.Time.Testing;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Validation;
using Shouldly;

namespace PaymentGateway.Api.Tests.Unit.Validation;

public class PostPaymentRequestValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostPaymentRequestValidator _validator = new(new FakeTimeProvider(Now));

    [Fact]
    public void Validate_ValidRequest_Passes()
    {
        var result = _validator.Validate(ValidRequest());

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("12345678901234")]
    [InlineData("1234567890123456789")]
    public void Validate_CardNumberLengthWithinBounds_Passes(string cardNumber)
    {
        var result = _validator.Validate(ValidRequest(cardNumber: cardNumber));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("1234567890123")]
    [InlineData("12345678901234567890")]
    [InlineData("1234567890123abc")]
    [InlineData("1234 5678 9012 3456")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_InvalidCardNumber_Fails(string? cardNumber)
    {
        var result = _validator.Validate(ValidRequest(cardNumber: cardNumber));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(PostPaymentRequest.CardNumber));
    }

    [Fact]
    public void Validate_InvalidCardNumber_ErrorMessageDoesNotEchoTheValue()
    {
        const string cardNumber = "1234567890123abc";

        var result = _validator.Validate(ValidRequest(cardNumber: cardNumber));

        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldAllBe(e => !e.ErrorMessage.Contains(cardNumber));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void Validate_ExpiryMonthWithinBounds_Passes(int expiryMonth)
    {
        var result = _validator.Validate(ValidRequest(expiryMonth: expiryMonth));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(null)]
    public void Validate_InvalidExpiryMonth_Fails(int? expiryMonth)
    {
        var result = _validator.Validate(ValidRequest(expiryMonth: expiryMonth));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(PostPaymentRequest.ExpiryMonth));
    }

    [Theory]
    [InlineData(2026, 6)]
    [InlineData(2026, 7)]
    [InlineData(2027, 1)]
    public void Validate_ExpiryInCurrentMonthOrLater_Passes(int expiryYear, int expiryMonth)
    {
        var result = _validator.Validate(ValidRequest(expiryMonth: expiryMonth, expiryYear: expiryYear));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(2026, 5)]
    [InlineData(2025, 12)]
    public void Validate_ExpiryInThePast_FailsOnExpiryDate(int expiryYear, int expiryMonth)
    {
        var result = _validator.Validate(ValidRequest(expiryMonth: expiryMonth, expiryYear: expiryYear));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == "ExpiryDate");
    }

    [Fact]
    public void Validate_ExpiryYearMissing_FailsOnExpiryYear()
    {
        var result = _validator.Validate(ValidRequest(expiryYear: null));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(PostPaymentRequest.ExpiryYear));
    }

    [Fact]
    public void Validate_PastYearWithInvalidMonth_FailsOnExpiryMonthOnly()
    {
        var result = _validator.Validate(ValidRequest(expiryMonth: 13, expiryYear: 2020));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(PostPaymentRequest.ExpiryMonth));
    }

    [Theory]
    [InlineData("GBP")]
    [InlineData("USD")]
    [InlineData("EUR")]
    public void Validate_SupportedCurrency_Passes(string currency)
    {
        var result = _validator.Validate(ValidRequest(currency: currency));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("gbp")]
    [InlineData("GBPP")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_UnsupportedCurrency_Fails(string? currency)
    {
        var result = _validator.Validate(ValidRequest(currency: currency));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(PostPaymentRequest.Currency));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Validate_PositiveAmount_Passes(int amount)
    {
        var result = _validator.Validate(ValidRequest(amount: amount));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(null)]
    public void Validate_NonPositiveOrMissingAmount_Fails(int? amount)
    {
        var result = _validator.Validate(ValidRequest(amount: amount));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(PostPaymentRequest.Amount));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234")]
    [InlineData("012")]
    public void Validate_CvvOfThreeOrFourDigits_Passes(string cvv)
    {
        var result = _validator.Validate(ValidRequest(cvv: cvv));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("12a")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_InvalidCvv_Fails(string? cvv)
    {
        var result = _validator.Validate(ValidRequest(cvv: cvv));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(PostPaymentRequest.Cvv));
    }

    [Fact]
    public void Validate_InvalidCvv_ErrorMessageDoesNotEchoTheValue()
    {
        const string cvv = "12a";

        var result = _validator.Validate(ValidRequest(cvv: cvv));

        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldAllBe(e => !e.ErrorMessage.Contains(cvv));
    }

    private static PostPaymentRequest ValidRequest(string? cardNumber = "2222405343248877", int? expiryMonth = 4, int? expiryYear = 2030, string? currency = "GBP", int? amount = 100, string? cvv = "123") => new()
    {
        CardNumber = cardNumber,
        ExpiryMonth = expiryMonth,
        ExpiryYear = expiryYear,
        Currency = currency,
        Amount = amount,
        Cvv = cvv
    };
}
