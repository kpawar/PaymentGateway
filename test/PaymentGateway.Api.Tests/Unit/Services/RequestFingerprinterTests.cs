using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;

using Shouldly;

namespace PaymentGateway.Api.Tests.Unit.Services;

public class RequestFingerprinterTests
{
    private readonly RequestFingerprinter _fingerprinter = new("test-secret");

    [Fact]
    public void Compute_SameRequest_ReturnsSameFingerprint()
    {
        _fingerprinter.Compute(Request()).ShouldBe(_fingerprinter.Compute(Request()));
    }

    [Fact]
    public void Compute_DifferentCvv_ReturnsSameFingerprint()
    {
        _fingerprinter.Compute(Request(cvv: "123")).ShouldBe(_fingerprinter.Compute(Request(cvv: "999")));
    }

    [Theory]
    [InlineData("cardNumber")]
    [InlineData("expiryMonth")]
    [InlineData("expiryYear")]
    [InlineData("currency")]
    [InlineData("amount")]
    public void Compute_DifferentField_ReturnsDifferentFingerprint(string field)
    {
        var changed = field switch
        {
            "cardNumber" => Request(cardNumber: "2222405343248878"),
            "expiryMonth" => Request(expiryMonth: 5),
            "expiryYear" => Request(expiryYear: 2031),
            "currency" => Request(currency: "USD"),
            _ => Request(amount: 101)
        };

        _fingerprinter.Compute(changed).ShouldNotBe(_fingerprinter.Compute(Request()));
    }

    [Fact]
    public void Compute_DifferentSecret_ReturnsDifferentFingerprint()
    {
        var other = new RequestFingerprinter("another-secret");

        other.Compute(Request()).ShouldNotBe(_fingerprinter.Compute(Request()));
    }

    [Fact]
    public void Compute_ReturnsHexSha256AndDoesNotContainTheCardNumber()
    {
        var fingerprint = _fingerprinter.Compute(Request());

        fingerprint.Length.ShouldBe(64);
        fingerprint.ShouldAllBe(c => char.IsAsciiHexDigit(c));
        fingerprint.ShouldNotContain("2222405343248877");
    }

    [Fact]
    public void Compute_RequestWithMissingFields_DoesNotThrow()
    {
        Should.NotThrow(() => _fingerprinter.Compute(new PostPaymentRequest()));
    }

    private static PostPaymentRequest Request(
        string cardNumber = "2222405343248877", int expiryMonth = 4, int expiryYear = 2030,
        string currency = "GBP", int amount = 100, string cvv = "123") => new()
    {
        CardNumber = cardNumber,
        ExpiryMonth = expiryMonth,
        ExpiryYear = expiryYear,
        Currency = currency,
        Amount = amount,
        Cvv = cvv
    };
}
