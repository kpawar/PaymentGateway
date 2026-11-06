namespace PaymentGateway.Api.Services;

public sealed class RequestFingerprintOptions
{
    public const string SectionName = "RequestFingerprint";

    public string Secret { get; set; } = string.Empty;
}
