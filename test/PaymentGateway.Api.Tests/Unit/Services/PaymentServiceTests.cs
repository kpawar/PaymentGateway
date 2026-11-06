using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

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
    private readonly RequestFingerprinter _fingerprinter = new("test-secret");
    private readonly PaymentService _service;

    public PaymentServiceTests()
    {
        var timeProvider = new FakeTimeProvider(Now);
        _service = new PaymentService(
            _repository, new PostPaymentRequestValidator(timeProvider), _fingerprinter, timeProvider);
    }

    [Fact]
    public async Task ProcessAsync_InvalidRequest_ReturnsRejectedWithValidationErrors()
    {
        var result = await _service.ProcessAsync(Guid.NewGuid(), ExpiredRequest());

        var rejected = result.ShouldBeOfType<PaymentResult.Rejected>();
        rejected.PaymentId.ShouldNotBe(Guid.Empty);
        rejected.Errors.Keys.ShouldContain("ExpiryDate");
    }

    [Fact]
    public async Task ProcessAsync_InvalidRequest_StoresRejectedPaymentWithoutCardNumberOrCvv()
    {
        var idempotencyKey = Guid.NewGuid();
        var request = ExpiredRequest();

        var result = await _service.ProcessAsync(idempotencyKey, request);

        var paymentId = result.ShouldBeOfType<PaymentResult.Rejected>().PaymentId;
        var stored = await _repository.GetByIdAsync(paymentId);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(PaymentStatus.Rejected);
        stored.IdempotencyKey.ShouldBe(idempotencyKey);
        stored.RequestFingerprint.ShouldBe(_fingerprinter.Compute(request));
        stored.RejectionReasons.ShouldNotBeNull().ShouldContain("Card expiry date must be in the future.");
        stored.CreatedAt.ShouldBe(Now);
        stored.UpdatedAt.ShouldBe(Now);

        var serialised = JsonSerializer.Serialize(stored with { RequestFingerprint = "" });
        serialised.ShouldNotContain(request.CardNumber!);
        serialised.ShouldNotContain(request.Cvv!);
    }

    [Theory]
    [InlineData("2222405343248877", "8877")]
    [InlineData("123456", "3456")]
    [InlineData("1234", "1234")]
    [InlineData("123", null)]
    [InlineData("1234567890abcd", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task ProcessAsync_InvalidRequest_StoresLastFourOnlyWhenCardNumberIsFourOrMoreDigits(
        string? cardNumber, string? expectedLastFour)
    {
        var result = await _service.ProcessAsync(Guid.NewGuid(), ExpiredRequest(cardNumber));

        var paymentId = result.ShouldBeOfType<PaymentResult.Rejected>().PaymentId;
        var stored = await _repository.GetByIdAsync(paymentId);
        stored.ShouldNotBeNull().CardNumberLastFour.ShouldBe(expectedLastFour);
    }

    private static PostPaymentRequest ExpiredRequest(string? cardNumber = "2222405343248877") => new()
    {
        CardNumber = cardNumber,
        ExpiryMonth = 4,
        ExpiryYear = 2020,
        Currency = "GBP",
        Amount = 100,
        Cvv = "7391"
    };
}
