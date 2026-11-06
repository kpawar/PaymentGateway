using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Persistence;

using Shouldly;

namespace PaymentGateway.Api.Tests.Unit.Persistence;

public class InMemoryPaymentsRepositoryTests
{
    private readonly InMemoryPaymentsRepository _repository = new();

    [Fact]
    public async Task TryAddAsync_NewPayment_ReturnsTrue()
    {
        var payment = CreatePayment();

        var added = await _repository.TryAddAsync(payment);

        added.ShouldBeTrue();
    }

    [Fact]
    public async Task TryAddAsync_DuplicateIdempotencyKey_ReturnsFalseAndKeepsOriginal()
    {
        var idempotencyKey = Guid.NewGuid();
        var original = CreatePayment(idempotencyKey: idempotencyKey, status: PaymentStatus.Authorized);
        var duplicate = CreatePayment(idempotencyKey: idempotencyKey, status: PaymentStatus.Declined);
        await _repository.TryAddAsync(original);

        var added = await _repository.TryAddAsync(duplicate);

        added.ShouldBeFalse();
        (await _repository.GetByIdempotencyKeyAsync(idempotencyKey)).ShouldBe(original);
        (await _repository.GetByIdAsync(duplicate.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ExistingPayment_ReturnsPayment()
    {
        var payment = CreatePayment();
        await _repository.TryAddAsync(payment);

        var result = await _repository.GetByIdAsync(payment.Id);

        result.ShouldBe(payment);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        await _repository.TryAddAsync(CreatePayment());

        var result = await _repository.GetByIdAsync(Guid.NewGuid());

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_ExistingKey_ReturnsPayment()
    {
        var payment = CreatePayment();
        await _repository.TryAddAsync(payment);

        var result = await _repository.GetByIdempotencyKeyAsync(payment.IdempotencyKey);

        result.ShouldBe(payment);
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_UnknownKey_ReturnsNull()
    {
        await _repository.TryAddAsync(CreatePayment());

        var result = await _repository.GetByIdempotencyKeyAsync(Guid.NewGuid());

        result.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateAsync_ExistingPayment_ReplacesStoredPayment()
    {
        var pending = CreatePayment(status: PaymentStatus.Pending);
        await _repository.TryAddAsync(pending);
        var authorized = pending with { Status = PaymentStatus.Authorized, AuthorizationCode = "auth-code" };

        await _repository.UpdateAsync(authorized);

        (await _repository.GetByIdAsync(pending.Id)).ShouldBe(authorized);
        (await _repository.GetByIdempotencyKeyAsync(pending.IdempotencyKey)).ShouldBe(authorized);
    }

    [Fact]
    public async Task UpdateAsync_UnknownPayment_ThrowsKeyNotFoundException()
    {
        var payment = CreatePayment();

        await Should.ThrowAsync<KeyNotFoundException>(() => _repository.UpdateAsync(payment));
    }

    [Fact]
    public async Task TryAddAsync_ConcurrentRequestsWithSameIdempotencyKey_ExactlyOneSucceeds()
    {
        var idempotencyKey = Guid.NewGuid();
        var payments = Enumerable.Range(0, 100)
            .Select(_ => CreatePayment(idempotencyKey: idempotencyKey))
            .ToList();

        var results = await Task.WhenAll(payments.Select(p => Task.Run(() => _repository.TryAddAsync(p))));

        results.Count(added => added).ShouldBe(1);
        var stored = await _repository.GetByIdempotencyKeyAsync(idempotencyKey);
        stored.ShouldNotBeNull();
        (await _repository.GetByIdAsync(stored.Id)).ShouldBe(stored);
    }

    [Fact]
    public async Task TryAddAsync_ConcurrentRequestsWithDifferentIdempotencyKeys_AllSucceed()
    {
        var payments = Enumerable.Range(0, 100)
            .Select(_ => CreatePayment())
            .ToList();

        var results = await Task.WhenAll(payments.Select(p => Task.Run(() => _repository.TryAddAsync(p))));

        results.ShouldAllBe(added => added);
        foreach (var payment in payments)
        {
            (await _repository.GetByIdAsync(payment.Id)).ShouldBe(payment);
        }
    }

    private static Payment CreatePayment(
        Guid? idempotencyKey = null,
        PaymentStatus status = PaymentStatus.Authorized)
    {
        var now = DateTimeOffset.UtcNow;

        return new Payment
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid(),
            RequestFingerprint = "fingerprint",
            Status = status,
            CardNumberLastFour = "8877",
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 100,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
