using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

using PaymentGateway.Api.Bank;
using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;

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

        using var webApplicationFactory = new PaymentsApiFactory(Substitute.For<IAcquiringBankClient>());
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
        using var webApplicationFactory = new PaymentsApiFactory(Substitute.For<IAcquiringBankClient>());
        var client = webApplicationFactory.CreateClient();
        
        // Act
        var response = await client.GetAsync($"/api/Payments/{Guid.NewGuid()}");
        
        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(true, PaymentStatus.Authorized)]
    [InlineData(false, PaymentStatus.Declined)]
    public async Task PostsValidPaymentAndReturnsMaskedPaymentResponse(bool authorized, PaymentStatus status)
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(authorized, authorized ? "auth-code" : ""));
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var response = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest());
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paymentResponse = JsonSerializer.Deserialize<PaymentResponse>(body, JsonOptions);
        paymentResponse.ShouldNotBeNull();
        paymentResponse.Status.ShouldBe(status);
        paymentResponse.CardNumberLastFour.ShouldBe("8877");
        paymentResponse.ExpiryMonth.ShouldBe(4);
        paymentResponse.ExpiryYear.ShouldBe(2030);
        paymentResponse.Currency.ShouldBe("GBP");
        paymentResponse.Amount.ShouldBe(1050);
        body.ShouldNotContain("2222405343248877");
        body.ShouldNotContain("7391");
        body.ShouldNotContain("auth-code");
        await bank.Received(1).AuthorizeAsync(
            Arg.Is<BankPaymentRequest>(request =>
                request.CardNumber == "2222405343248877" && request.Cvv == "7391"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostsInvalidPaymentAndStoresRejectedAttempt()
    {
        var bank = Substitute.For<IAcquiringBankClient>();
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var response = await PostPaymentAsync(client, Guid.NewGuid().ToString(), ValidRequest(expiryYear: 2020));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paymentId = body.RootElement.GetProperty("paymentId").GetGuid();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
        body.RootElement.GetProperty("errors").GetProperty("ExpiryDate").GetArrayLength().ShouldBeGreaterThan(0);
        var stored = await factory.Services.GetRequiredService<IPaymentsRepository>().GetByIdAsync(paymentId);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(PaymentStatus.Rejected);
        await bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
    }

    [Fact]
    public async Task PostsBankFailureAsBadGatewayAndStoresFailedAttempt()
    {
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AcquiringBankUnavailableException("bank unavailable"));
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var response = await PostPaymentAsync(client, Guid.NewGuid().ToString(), ValidRequest());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paymentId = body.RootElement.GetProperty("paymentId").GetGuid();

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        body.RootElement.GetProperty("status").GetString().ShouldBe("Failed");
        var stored = await factory.Services.GetRequiredService<IPaymentsRepository>().GetByIdAsync(paymentId);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(PaymentStatus.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task MissingOrInvalidIdempotencyKeyReturnsBadRequestWithoutStoring(string? idempotencyKey)
    {
        var bank = Substitute.For<IAcquiringBankClient>();
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var response = await PostPaymentAsync(client, idempotencyKey, ValidRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await factory.Services.GetRequiredService<IPaymentsRepository>()
            .GetByIdempotencyKeyAsync(Guid.Empty)).ShouldBeNull();
        await bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
    }

    [Fact]
    public async Task InvalidJsonReturnsBadRequestWithoutReservingIdempotencyKey()
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/Payments")
        {
            Content = new StringContent("""{"amount":"not-an-integer"}""", System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString());

        using var response = await client.SendAsync(message);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await factory.Services.GetRequiredService<IPaymentsRepository>()
            .GetByIdempotencyKeyAsync(idempotencyKey)).ShouldBeNull();
        await bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
    }

    [Fact]
    public async Task RepostingSameKeyAndBodyReplaysResponseWithoutCallingBankAgain()
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(true, "auth-code"));
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var first = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest());
        var firstBody = await first.Content.ReadAsStringAsync();
        using var second = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest());
        var secondBody = await second.Content.ReadAsStringAsync();

        second.StatusCode.ShouldBe(first.StatusCode);
        secondBody.ShouldBe(firstBody);
        await bank.Received(1).AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReusingKeyWithDifferentRequestReturnsUnprocessableEntity()
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(true, "auth-code"));
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        using var first = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest());
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest(amount: 999));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await bank.Received(1).AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepostingWhilePaymentIsPendingReturnsConflict()
    {
        var idempotencyKey = Guid.NewGuid();
        var request = ValidRequest();
        var bank = Substitute.For<IAcquiringBankClient>();
        using var factory = new PaymentsApiFactory(bank);
        var repository = factory.Services.GetRequiredService<IPaymentsRepository>();
        var now = DateTimeOffset.UtcNow;
        await repository.TryAddAsync(new Payment
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = new RequestFingerprinter(PaymentsApiFactory.FingerprintSecret).Compute(request),
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        });
        var client = factory.CreateClient();

        using var response = await PostPaymentAsync(client, idempotencyKey.ToString(), request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
    }

    [Theory]
    [InlineData(true, PaymentStatus.Authorized)]
    [InlineData(false, PaymentStatus.Declined)]
    public async Task PostedPaymentCanBeRetrieved(bool authorized, PaymentStatus status)
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(authorized, authorized ? "auth-code" : ""));
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        using var postResponse = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest());
        var posted = JsonSerializer.Deserialize<PaymentResponse>(
            await postResponse.Content.ReadAsStringAsync(), JsonOptions);
        posted.ShouldNotBeNull();

        using var getResponse = await client.GetAsync($"/api/Payments/{posted.Id}");
        var retrieved = JsonSerializer.Deserialize<PaymentResponse>(
            await getResponse.Content.ReadAsStringAsync(), JsonOptions);

        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retrieved.ShouldBe(posted with { Status = status });
    }

    [Theory]
    [InlineData(true, PaymentStatus.Rejected)]
    [InlineData(false, PaymentStatus.Failed)]
    public async Task RejectedOrFailedPaymentCanBeRetrieved(bool isRejected, PaymentStatus status)
    {
        var bank = Substitute.For<IAcquiringBankClient>();
        if (!isRejected)
        {
            bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new AcquiringBankUnavailableException("bank unavailable"));
        }

        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        var request = ValidRequest(expiryYear: isRejected ? 2020 : 2030);
        using var postResponse = await PostPaymentAsync(client, Guid.NewGuid().ToString(), request);
        using var postBody = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var paymentId = postBody.RootElement.GetProperty("paymentId").GetGuid();

        using var getResponse = await client.GetAsync($"/api/Payments/{paymentId}");
        var retrieved = JsonSerializer.Deserialize<PaymentResponse>(
            await getResponse.Content.ReadAsStringAsync(), JsonOptions);

        postResponse.StatusCode.ShouldBe(isRejected ? HttpStatusCode.BadRequest : HttpStatusCode.BadGateway);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retrieved.ShouldNotBeNull().Id.ShouldBe(paymentId);
        retrieved.Status.ShouldBe(status);
        retrieved.CardNumberLastFour.ShouldBe("8877");
        retrieved.ExpiryMonth.ShouldBe(4);
        retrieved.ExpiryYear.ShouldBe(request.ExpiryYear);
        retrieved.Currency.ShouldBe("GBP");
        retrieved.Amount.ShouldBe(1050);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.BadRequest)]
    [InlineData(false, HttpStatusCode.BadGateway)]
    public async Task RepostingRejectedOrFailedPaymentReplaysOriginalResponse(
        bool isRejected, HttpStatusCode expectedStatus)
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        if (!isRejected)
        {
            bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new AcquiringBankUnavailableException("bank unavailable"));
        }

        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        var request = ValidRequest(expiryYear: isRejected ? 2020 : 2030);
        using var first = await PostPaymentAsync(client, idempotencyKey.ToString(), request);
        var firstBody = await first.Content.ReadAsStringAsync();
        using var replay = await PostPaymentAsync(client, idempotencyKey.ToString(), request);

        first.StatusCode.ShouldBe(expectedStatus);
        replay.StatusCode.ShouldBe(first.StatusCode);
        (await replay.Content.ReadAsStringAsync()).ShouldBe(firstBody);
        if (isRejected)
        {
            await bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
        }
        else
        {
            await bank.Received(1).AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>());
        }
    }

    private static async Task<HttpResponseMessage> PostPaymentAsync(
        HttpClient client, string? idempotencyKey, PostPaymentRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/Payments")
        {
            Content = JsonContent.Create(request)
        };
        if (idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(message);
    }

    private static PostPaymentRequest ValidRequest(int expiryYear = 2030, int amount = 1050) => new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 4,
        ExpiryYear = expiryYear,
        Currency = "GBP",
        Amount = amount,
        Cvv = "7391"
    };

    private sealed class PaymentsApiFactory(IAcquiringBankClient bank) : WebApplicationFactory<Program>
    {
        public const string FingerprintSecret = "integration-test-secret-0123456789abcdef";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AcquiringBank:BaseUrl"] = "http://localhost:8080",
                    ["AcquiringBank:Timeout"] = "00:00:05",
                    ["RequestFingerprint:Secret"] = FingerprintSecret
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAcquiringBankClient>();
                services.AddSingleton(bank);
            });
        }
    }
}
