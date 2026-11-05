using System.Collections.Concurrent;

using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Persistence;

public class InMemoryPaymentsRepository
{
    private readonly ConcurrentDictionary<Guid, Payment> _paymentsById = new();

    private readonly ConcurrentDictionary<Guid, Payment> _paymentsByIdempotencyKey = new();

    public Task<bool> TryAddAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        // Claiming the idempotency key is the atomic step; only the winner is stored by id.
        if (!_paymentsByIdempotencyKey.TryAdd(payment.IdempotencyKey, payment))
        {
            return Task.FromResult(false);
        }

        _paymentsById[payment.Id] = payment;

        return Task.FromResult(true);
    }

    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _paymentsById.TryGetValue(id, out var payment);

        return Task.FromResult(payment);
    }

    public Task<Payment?> GetByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        _paymentsByIdempotencyKey.TryGetValue(idempotencyKey, out var payment);

        return Task.FromResult(payment);
    }

    public Task UpdateAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
