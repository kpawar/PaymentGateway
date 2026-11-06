using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Validation;
using Shouldly;

namespace PaymentGateway.Api.Tests.Unit.Validation;

public class PostPaymentRequestValidatorTests
{
    private readonly PostPaymentRequestValidator _validator = new();

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

    private static PostPaymentRequest ValidRequest(string? cardNumber = "2222405343248877", int? expiryMonth = 4) => new()
    {
        CardNumber = cardNumber,
        ExpiryMonth = expiryMonth,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };
}
