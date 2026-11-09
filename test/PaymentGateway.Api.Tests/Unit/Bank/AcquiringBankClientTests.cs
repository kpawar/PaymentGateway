using System.Net;
using System.Text.Json.Nodes;

using PaymentGateway.Api.Bank;

using Shouldly;

namespace PaymentGateway.Api.Tests.Unit.Bank;

public class AcquiringBankClientTests
{
    private static readonly BankPaymentRequest Request = new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 4,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    private readonly StubHttpMessageHandler _handler = new();

    private static AcquiringBankClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") });

    [Fact]
    public async Task AuthorizeAsync_PostsSnakeCaseBodyWithFormattedExpiryDateToPayments()
    {
        await CreateClient(_handler).AuthorizeAsync(Request);

        _handler.Request!.Method.ShouldBe(HttpMethod.Post);
        _handler.Request.RequestUri!.AbsoluteUri.ShouldBe("http://localhost:8080/payments");
        JsonNode.DeepEquals(JsonNode.Parse(_handler.RequestBody!), JsonNode.Parse("""
            {
              "card_number": "2222405343248877",
              "expiry_date": "04/2030",
              "currency": "GBP",
              "amount": 100,
              "cvv": "123"
            }
            """)).ShouldBeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_BankAuthorizes_ReturnsAuthorizedWithCode()
    {
        var handler = new StubHttpMessageHandler(responseBody: """{"authorized": true, "authorization_code": "0bb07405-6d44-4b50-a14f-7ae0beff13ad"}""");

        var response = await CreateClient(handler).AuthorizeAsync(Request);

        response.Authorized.ShouldBeTrue();
        response.AuthorizationCode.ShouldBe("0bb07405-6d44-4b50-a14f-7ae0beff13ad");
    }

    [Fact]
    public async Task AuthorizeAsync_BankDeclines_ReturnsNotAuthorized()
    {
        var handler = new StubHttpMessageHandler(responseBody: """{"authorized": false, "authorization_code": ""}""");

        var response = await CreateClient(handler).AuthorizeAsync(Request);

        response.Authorized.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task AuthorizeAsync_BankReturnsNonSuccessStatus_ThrowsAcquiringBankUnavailable(HttpStatusCode statusCode)
    {
        var handler = new StubHttpMessageHandler(statusCode);

        var act = () => CreateClient(handler).AuthorizeAsync(Request);

        await act.ShouldThrowAsync<AcquiringBankUnavailableException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"authorization_code": "0bb07405-6d44-4b50-a14f-7ae0beff13ad"}""")]
    public async Task AuthorizeAsync_BankReturnsSuccessWithUnusableBody_ThrowsAcquiringBankUnavailable(string responseBody)
    {
        var handler = new StubHttpMessageHandler(responseBody: responseBody);

        var act = () => CreateClient(handler).AuthorizeAsync(Request);

        await act.ShouldThrowAsync<AcquiringBankUnavailableException>();
    }

    [Fact]
    public async Task AuthorizeAsync_ConnectionFails_ThrowsAcquiringBankUnavailable()
    {
        var handler = new StubHttpMessageHandler(exceptionToThrow: new HttpRequestException("Connection refused"));

        var act = () => CreateClient(handler).AuthorizeAsync(Request);

        await act.ShouldThrowAsync<AcquiringBankUnavailableException>();
    }

    [Fact]
    public async Task AuthorizeAsync_BankTimesOut_ThrowsAcquiringBankUnavailable()
    {
        var handler = new StubHttpMessageHandler(exceptionToThrow: new TaskCanceledException("The request timed out."));

        var act = () => CreateClient(handler).AuthorizeAsync(Request);

        await act.ShouldThrowAsync<AcquiringBankUnavailableException>();
    }

    [Fact]
    public async Task AuthorizeAsync_CallerCancels_PropagatesCancellationInsteadOfReportingBankUnavailable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CreateClient(_handler).AuthorizeAsync(Request, cts.Token);

        var exception = await Should.ThrowAsync<Exception>(act);
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }
}
