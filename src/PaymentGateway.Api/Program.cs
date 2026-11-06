using System.Text;
using System.Text.Json.Serialization;

using FluentValidation;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Bank;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;
using PaymentGateway.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IPaymentsRepository, InMemoryPaymentsRepository>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IValidator<PostPaymentRequest>, PostPaymentRequestValidator>();
builder.Services.AddScoped<IPaymentService, PaymentService>();

builder.Services.AddOptions<AcquiringBankOptions>()
    .BindConfiguration(AcquiringBankOptions.SectionName)
    .Validate(options =>
            Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseAddress) &&
            (baseAddress.Scheme == Uri.UriSchemeHttp || baseAddress.Scheme == Uri.UriSchemeHttps),
        "AcquiringBank:BaseUrl must be an absolute HTTP or HTTPS URL.")
    .Validate(options => options.Timeout > TimeSpan.Zero, "AcquiringBank:Timeout must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddOptions<RequestFingerprintOptions>()
    .BindConfiguration(RequestFingerprintOptions.SectionName)
    .Validate(options =>
            !string.IsNullOrWhiteSpace(options.Secret) && Encoding.UTF8.GetByteCount(options.Secret) >= 32,
        "RequestFingerprint:Secret must be at least 32 bytes.")
    .ValidateOnStart();

builder.Services.AddSingleton(serviceProvider =>
    new RequestFingerprinter(serviceProvider.GetRequiredService<IOptions<RequestFingerprintOptions>>().Value.Secret));

builder.Services.AddHttpClient<IAcquiringBankClient, AcquiringBankClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<AcquiringBankOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = options.Timeout;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposes the implicit Program class to WebApplicationFactory<Program> in the test project.
public partial class Program;
