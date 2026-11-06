using System.Text.Json;

namespace PaymentGateway.Api.Bank;

public class AcquiringBankClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<BankPaymentResponse> AuthorizeAsync(BankPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var body = new BankRequestBody(
            request.CardNumber,
            $"{request.ExpiryMonth:D2}/{request.ExpiryYear}",
            request.Currency,
            request.Amount,
            request.Cvv);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync("/payments", body, JsonOptions, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new AcquiringBankUnavailableException("Could not reach the acquiring bank.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not cancelled by the caller, so the HttpClient timed out.
            throw new AcquiringBankUnavailableException("The acquiring bank timed out.", exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AcquiringBankUnavailableException($"Acquiring bank returned {(int)response.StatusCode}.");
        }

        return (await response.Content.ReadFromJsonAsync<BankPaymentResponse>(JsonOptions, cancellationToken))!;
    }

    // The bank's wire format: serialised as snake_case, with expiry as "MM/YYYY".
    private record BankRequestBody(string CardNumber, string ExpiryDate, string Currency, int Amount, string Cvv);
}
