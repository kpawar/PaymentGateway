using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Persistence;

using Shouldly;

namespace PaymentGateway.Api.Tests.Integration;

public class PaymentsControllerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task RetrievesAPaymentSuccessfully()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            RequestFingerprint = "fingerprint",
            Status = PaymentStatus.Authorized,
            CardNumberLastFour = "0123",
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 1050,
            AuthorizationCode = "auth-code",
            CreatedAt = now,
            UpdatedAt = now
        };

        var webApplicationFactory = new WebApplicationFactory<Program>();
        var repository = webApplicationFactory.Services.GetRequiredService<IPaymentsRepository>();
        await repository.TryAddAsync(payment);
        var client = webApplicationFactory.CreateClient();

        // Act
        var response = await client.GetAsync($"/api/Payments/{payment.Id}");
        var body = await response.Content.ReadAsStringAsync();
        var paymentResponse = JsonSerializer.Deserialize<PaymentResponse>(body, JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        paymentResponse.ShouldBe(new PaymentResponse
        {
            Id = payment.Id,
            Status = PaymentStatus.Authorized,
            CardNumberLastFour = "0123",
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 1050
        });
        body.ShouldContain("\"status\":\"Authorized\"");
        body.ShouldNotContain("auth-code");
        body.ShouldNotContain("fingerprint");
    }

    [Fact]
    public async Task Returns404IfPaymentNotFound()
    {
        // Arrange
        var webApplicationFactory = new WebApplicationFactory<Program>();
        var client = webApplicationFactory.CreateClient();
        
        // Act
        var response = await client.GetAsync($"/api/Payments/{Guid.NewGuid()}");
        
        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
