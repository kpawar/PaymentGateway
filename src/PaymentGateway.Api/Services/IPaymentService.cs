using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

public interface IPaymentService
{
    Task<PaymentResult> ProcessAsync(
        Guid idempotencyKey, PostPaymentRequest request, CancellationToken cancellationToken = default);
}
