using Claims.Filters;
using Claims.Infrastructure;
using Claims.Services;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var databases = new LocalDatabases();
await databases.StartAsync();

// Add services to the container.
builder.Services
    .AddControllers(options =>
    {
        options.Filters.Add<ValidationExceptionFilter>();
        options.Filters.Add<ResourceNotFoundExceptionFilter>();
    })
    .AddJsonOptions(x =>
    {
        x.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddClaimsInfrastructure(databases.SqlConnectionString,
    databases.MongoConnectionString, builder.Configuration["MongoDb:DatabaseName"]!);
builder.Services.AddScoped<IClaimService, ClaimService>();
builder.Services.AddScoped<ICoverService, CoverService>();
builder.Services.AddSingleton<PremiumCalculator>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Services.ApplyAuditMigrations();

app.Run();

public partial class Program { }
