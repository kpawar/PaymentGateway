using System.ComponentModel.DataAnnotations;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentsRepository _paymentsRepository;
    private readonly IPaymentService _paymentService;

    public PaymentsController(IPaymentsRepository paymentsRepository, IPaymentService paymentService)
    {
        _paymentsRepository = paymentsRepository;
        _paymentService = paymentService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PostPaymentAsync(
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey,
        [FromBody] PostPaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out var parsedKey))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Idempotency-Key",
                detail: "A valid GUID Idempotency-Key header is required.");
        }

        var result = await _paymentService.ProcessAsync(parsedKey, request, cancellationToken);

        return result switch
        {
            PaymentResult.Processed processed => Ok(PaymentResponse.From(processed.Payment)),
            PaymentResult.Rejected rejected => CreateValidationProblem(rejected),
            PaymentResult.BankUnavailable unavailable => CreateBankUnavailableProblem(unavailable),
            PaymentResult.IdempotencyConflict => Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency key conflict",
                detail: "The Idempotency-Key has already been used with a different payment request."),
            PaymentResult.InProgress => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Payment is still processing",
                detail: "A payment with this Idempotency-Key is still being processed."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown payment result.")
        };
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentResponse>> GetPaymentAsync(Guid id, CancellationToken cancellationToken)
    {
        var payment = await _paymentsRepository.GetByIdAsync(id, cancellationToken);

        if (payment is null)
        {
            return NotFound();
        }

        return Ok(PaymentResponse.From(payment));
    }

    // Problem Details already defines "status" as the HTTP status code, so the payment outcome goes in "paymentStatus".
    // Both problems are created by the ProblemDetailsFactory so they get the same "type" and "traceId" as other errors.
    private IActionResult CreateValidationProblem(PaymentResult.Rejected rejected)
    {
        // Error keys use the same camelCase names as the JSON request body, not the C# property names.
        var modelState = new ModelStateDictionary();
        foreach (var (field, messages) in rejected.Errors)
        {
            foreach (var message in messages)
            {
                modelState.AddModelError(JsonNamingPolicy.CamelCase.ConvertName(field), message);
            }
        }

        var problem = ProblemDetailsFactory.CreateValidationProblemDetails(
            HttpContext, modelState, StatusCodes.Status400BadRequest);
        problem.Extensions["paymentStatus"] = nameof(PaymentStatus.Rejected);

        return new ObjectResult(problem) { StatusCode = problem.Status };
    }

    private IActionResult CreateBankUnavailableProblem(PaymentResult.BankUnavailable unavailable)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            StatusCodes.Status502BadGateway,
            title: "Acquiring bank unavailable",
            type: "https://tools.ietf.org/html/rfc9110#section-15.6.3");
        problem.Extensions["paymentId"] = unavailable.PaymentId;
        problem.Extensions["paymentStatus"] = nameof(PaymentStatus.Failed);

        return new ObjectResult(problem) { StatusCode = problem.Status };
    }
}
