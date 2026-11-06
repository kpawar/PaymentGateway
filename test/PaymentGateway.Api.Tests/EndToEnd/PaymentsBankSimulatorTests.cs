using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;

using Shouldly;

namespace PaymentGateway.Api.Tests.EndToEnd;

[Trait("Category", "BankSimulator")]
public class PaymentsBankSimulatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("2222405343248871", HttpStatusCode.OK, PaymentStatus.Authorized)]
    [InlineData("2222405343248872", HttpStatusCode.OK, PaymentStatus.Declined)]
    [InlineData("2222405343248870", HttpStatusCode.BadGateway, PaymentStatus.Failed)]
    public async Task PostPaymentAgainstSimulator_StoresOutcomeAndCanRetrieveIt(
        string cardNumber, HttpStatusCode expectedPostStatus, PaymentStatus expectedPaymentStatus)
    {
        using var factory = new SimulatorApiFactory();
        using var client = factory.CreateClient();
        var idempotencyKey = Guid.NewGuid();
        var request = new PostPaymentRequest
        {
            CardNumber = cardNumber,
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 1050,
            Cvv = "7391"
        };
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Payments")
        {
            Content = JsonContent.Create(request)
        };
        postRequest.Headers.Add("Idempotency-Key", idempotencyKey.ToString());

        using var postResponse = await client.SendAsync(postRequest);
        var postBody = await postResponse.Content.ReadAsStringAsync();

        postResponse.StatusCode.ShouldBe(expectedPostStatus);
        Guid paymentId;

        if (expectedPaymentStatus is PaymentStatus.Authorized or PaymentStatus.Declined)
        {
            var postPayment = JsonSerializer.Deserialize<PaymentResponse>(postBody, JsonOptions);
            postPayment.ShouldNotBeNull();
            paymentId = postPayment.Id;
            postPayment.Status.ShouldBe(expectedPaymentStatus);
            postPayment.CardNumberLastFour.ShouldBe(cardNumber[^4..]);
            postPayment.ExpiryMonth.ShouldBe(request.ExpiryMonth);
            postPayment.ExpiryYear.ShouldBe(request.ExpiryYear);
            postPayment.Currency.ShouldBe(request.Currency);
            postPayment.Amount.ShouldBe(request.Amount);
            postBody.ShouldNotContain(cardNumber);
            postBody.ShouldNotContain(request.Cvv!);
        }
        else
        {
            using var errorBody = JsonDocument.Parse(postBody);
            errorBody.RootElement.GetProperty("status").GetString().ShouldBe("Failed");
            paymentId = errorBody.RootElement.GetProperty("paymentId").GetGuid();
        }

        using var getResponse = await client.GetAsync($"/api/Payments/{paymentId}");
        var getBody = await getResponse.Content.ReadAsStringAsync();
        var retrieved = JsonSerializer.Deserialize<PaymentResponse>(getBody, JsonOptions);

        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retrieved.ShouldNotBeNull();
        retrieved.Id.ShouldBe(paymentId);
        retrieved.Status.ShouldBe(expectedPaymentStatus);
        retrieved.CardNumberLastFour.ShouldBe(cardNumber[^4..]);
        retrieved.ExpiryMonth.ShouldBe(request.ExpiryMonth);
        retrieved.ExpiryYear.ShouldBe(request.ExpiryYear);
        retrieved.Currency.ShouldBe(request.Currency);
        retrieved.Amount.ShouldBe(request.Amount);
        getBody.ShouldNotContain(cardNumber);
        getBody.ShouldNotContain(request.Cvv!);
    }

    private sealed class SimulatorApiFactory : WebApplicationFactory<Program>
    {
        private const string FingerprintSecret = "simulator-end-to-end-test-secret-012345";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AcquiringBank:BaseUrl"] = "http://localhost:8080",
                    ["AcquiringBank:Timeout"] = "00:00:05",
                    ["RequestFingerprint:Secret"] = FingerprintSecret
                }));
        }
    }
}
