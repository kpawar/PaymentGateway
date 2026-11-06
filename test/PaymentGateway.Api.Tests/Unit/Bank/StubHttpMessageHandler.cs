using System.Net;

namespace PaymentGateway.Api.Tests.Unit.Bank;

/// <summary>
/// Returns a canned response (or throws the given exception) and records the request it received, including its body.
/// </summary>
public class StubHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.OK, string responseBody = "{}", Exception? exceptionToThrow = null)
    : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;

        if (exceptionToThrow is not null)
        {
            throw exceptionToThrow;
        }

        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
