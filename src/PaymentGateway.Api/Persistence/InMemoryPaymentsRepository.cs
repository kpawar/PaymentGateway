using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Persistence;

public class InMemoryPaymentsRepository
{
    public Task<bool> TryAddAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<Payment?> GetByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task UpdateAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
