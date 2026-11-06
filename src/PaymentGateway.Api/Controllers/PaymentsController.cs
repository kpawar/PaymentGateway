using Microsoft.AspNetCore.Mvc;

using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class PaymentsController : Controller
{
    private readonly IPaymentsRepository _paymentsRepository;

    public PaymentsController(IPaymentsRepository paymentsRepository)
    {
        _paymentsRepository = paymentsRepository;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PaymentResponse>> GetPaymentAsync(Guid id, CancellationToken cancellationToken)
    {
        var payment = await _paymentsRepository.GetByIdAsync(id, cancellationToken);

        if (payment is null)
        {
            return NotFound();
        }

        return Ok(PaymentResponse.From(payment));
    }
}
