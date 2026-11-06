namespace PaymentGateway.Api.Domain;

public enum PaymentStatus
{
    Pending,
    Authorized,
    Declined,
    Rejected,
    Failed
}