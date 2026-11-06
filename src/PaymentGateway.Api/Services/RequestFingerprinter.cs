using System.Security.Cryptography;
using System.Text;

using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Produces the idempotency fingerprint of a payment request: an HMAC-SHA256 (hex) over card number, expiry,
/// currency and amount. The CVV is excluded because it must never be retained. The card number is only hashed,
/// and the secret stops it being brute-forced from the hash.
/// </summary>
public class RequestFingerprinter(string secret)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    public string Compute(PostPaymentRequest request)
    {
        var data = string.Join('|', request.CardNumber, request.ExpiryMonth, request.ExpiryYear, request.Currency, request.Amount);

        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data)));
    }
}
