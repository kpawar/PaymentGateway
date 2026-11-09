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
    public async Task PostsInvalidPaymentWithoutCreatingPaymentRecord()
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var response = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest(expiryYear: 2020));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        AssertPaymentProblem(response, body, HttpStatusCode.BadRequest, PaymentStatus.Rejected);
        body.RootElement.GetProperty("errors").GetProperty("expiryDate").GetArrayLength().ShouldBeGreaterThan(0);
        body.RootElement.TryGetProperty("paymentId", out _).ShouldBeFalse();
        (await factory.Services.GetRequiredService<IPaymentsRepository>()
            .GetByIdempotencyKeyAsync(idempotencyKey)).ShouldBeNull();
        await bank.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!);
    }

    [Fact]
    public async Task ValidationErrorKeysMatchTheCamelCaseRequestFieldNames()
    {
        using var factory = new PaymentsApiFactory(Substitute.For<IAcquiringBankClient>());
        var client = factory.CreateClient();
        var request = new PostPaymentRequest
        {
            CardNumber = "123", ExpiryMonth = 13, ExpiryYear = 2030, Currency = "JPY", Amount = 0, Cvv = "12"
        };

        using var response = await PostPaymentAsync(client, Guid.NewGuid().ToString(), request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name)
            .ShouldBe(["cardNumber", "expiryMonth", "currency", "amount", "cvv"], ignoreOrder: true);

        using var expired = await PostPaymentAsync(client, Guid.NewGuid().ToString(), ValidRequest(expiryYear: 2020));
        using var expiredBody = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
        expiredBody.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name)
            .ShouldBe(["expiryDate"]);
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
        AssertPaymentProblem(response, body, HttpStatusCode.BadGateway, PaymentStatus.Failed);
        var stored = await factory.Services.GetRequiredService<IPaymentsRepository>().GetByIdAsync(paymentId);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(PaymentStatus.Failed);
    }

    [Fact]
    public async Task UnexpectedExceptionReturnsGenericProblemDetails()
    {
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("internal failure detail"));
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var response = await PostPaymentAsync(client, Guid.NewGuid().ToString(), ValidRequest());
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe((int)HttpStatusCode.InternalServerError);
        body.ShouldNotContain("internal failure detail");
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

    [Theory]
    [InlineData("""{"amount":"not-an-integer"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task UnreadableBodyReturnsGenericBadRequestWithoutReservingIdempotencyKey(string requestBody)
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/Payments")
        {
            Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString());

        using var response = await client.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);

        // Serializer internals (.NET type names, JSON paths) are not part of the merchant-facing contract.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Invalid request body");
        problem.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.RootElement.TryGetProperty("errors", out _).ShouldBeFalse();
        problem.RootElement.TryGetProperty("paymentStatus", out _).ShouldBeFalse();
        body.ShouldNotContain("System.");
        body.ShouldNotContain("$.");
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

    [Fact]
    public async Task FailedPaymentCanBeRetrieved()
    {
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AcquiringBankUnavailableException("bank unavailable"));

        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        var request = ValidRequest();
        using var postResponse = await PostPaymentAsync(client, Guid.NewGuid().ToString(), request);
        using var postBody = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var paymentId = postBody.RootElement.GetProperty("paymentId").GetGuid();

        using var getResponse = await client.GetAsync($"/api/Payments/{paymentId}");
        var retrieved = JsonSerializer.Deserialize<PaymentResponse>(
            await getResponse.Content.ReadAsStringAsync(), JsonOptions);

        postResponse.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retrieved.ShouldNotBeNull().Id.ShouldBe(paymentId);
        retrieved.Status.ShouldBe(PaymentStatus.Failed);
        retrieved.CardNumberLastFour.ShouldBe("8877");
        retrieved.ExpiryMonth.ShouldBe(4);
        retrieved.ExpiryYear.ShouldBe(2030);
        retrieved.Currency.ShouldBe("GBP");
        retrieved.Amount.ShouldBe(1050);
    }

    [Fact]
    public async Task UnexpectedBankCallFailureIsStoredAsFailedAndReplaysAsBadGateway()
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("internal failure detail"));
        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();

        using var first = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest());
        var stored = await factory.Services.GetRequiredService<IPaymentsRepository>()
            .GetByIdempotencyKeyAsync(idempotencyKey);
        using var getResponse = await client.GetAsync($"/api/Payments/{stored?.Id}");
        var retrieved = JsonSerializer.Deserialize<PaymentResponse>(
            await getResponse.Content.ReadAsStringAsync(), JsonOptions);
        using var replay = await PostPaymentAsync(client, idempotencyKey.ToString(), ValidRequest());
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        first.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        stored.ShouldNotBeNull().Status.ShouldBe(PaymentStatus.Failed);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retrieved.ShouldNotBeNull().Status.ShouldBe(PaymentStatus.Failed);
        retrieved.CardNumberLastFour.ShouldBe("8877");
        replay.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        replayBody.RootElement.GetProperty("paymentId").GetGuid().ShouldBe(stored.Id);
        AssertPaymentProblem(replay, replayBody, HttpStatusCode.BadGateway, PaymentStatus.Failed);
        await bank.ReceivedWithAnyArgs(1).AuthorizeAsync(default!);
    }

    [Fact]
    public async Task RejectedRequestDoesNotReserveKeyAndCanBeCorrected()
    {
        var idempotencyKey = Guid.NewGuid();
        var bank = Substitute.For<IAcquiringBankClient>();
        bank.AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse(true, "auth-code"));

        using var factory = new PaymentsApiFactory(bank);
        var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPaymentsRepository>();

        using var rejected = await PostPaymentAsync(
            client, idempotencyKey.ToString(), ValidRequest(expiryYear: 2020));
        using var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        using var corrected = await PostPaymentAsync(
            client, idempotencyKey.ToString(), ValidRequest());

        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        AssertPaymentProblem(rejected, rejectedBody, HttpStatusCode.BadRequest, PaymentStatus.Rejected);
        rejectedBody.RootElement.TryGetProperty("paymentId", out _).ShouldBeFalse();
        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await repository.GetByIdempotencyKeyAsync(idempotencyKey))?.Status.ShouldBe(PaymentStatus.Authorized);
        await bank.Received(1).AuthorizeAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>());
    }

    // A payment problem keeps the standard numeric "status" (the HTTP code) exactly once, carries the payment
    // outcome in "paymentStatus", and has the same "type" and "traceId" as every other Problem Details response.
    private static void AssertPaymentProblem(
        HttpResponseMessage response, JsonDocument body, HttpStatusCode statusCode, PaymentStatus paymentStatus)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var root = body.RootElement;
        root.EnumerateObject().Count(property => property.Name == "status").ShouldBe(1);
        root.GetProperty("status").GetInt32().ShouldBe((int)statusCode);
        root.GetProperty("paymentStatus").GetString().ShouldBe(paymentStatus.ToString());
        root.GetProperty("type").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
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
