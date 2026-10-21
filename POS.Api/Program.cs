using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using POS.Api.Models;
using Scalar.AspNetCore;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.MaxDepth = 32;
    });
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
// builder.Services.AddOpenApi(options =>
// {
//     options.ShouldInclude = (apiDescription) => false;
// });
builder.Services.AddEndpointsApiExplorer();
// builder.Services.AddSwaggerGen();
// // builder.Services.AddSwaggerGen();
// app.UseSwagger();
// app.UseSwaggerUI(...)

// Add PostgreSQL DbContext
builder.Services.AddDbContext<PosDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<POS.Shared.Interfaces.IReceiptService, POS.Api.Services.ReceiptService>();
builder.Services.AddScoped<POS.Shared.Interfaces.ISequenceService, POS.Api.Services.SequenceService>();
builder.Services.AddScoped<POS.Api.Services.IDiscountCalculationService, POS.Api.Services.DiscountCalculationService>();
builder.Services.AddScoped<POS.Shared.Interfaces.ISalesReportService, POS.Api.Services.SalesReportService>();

// Fix for Npgsql DateTime handling if using local DateTime values
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Add CORS so your MAUI app can communicate with this API
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Legacy schema changes must be explicitly requested; never migrate production on startup.
if (builder.Configuration.GetValue<bool>("Database:RunLegacyMigrations"))
{
    POS.Api.MigrationRunner.Run(builder.Configuration.GetConnectionString("DefaultConnection"));
}

// app.UseRouting();
app.UseStaticFiles();
app.UseCors("AllowAll"); // Enable CORS

app.UseAuthentication();
app.UseAuthorization();

// // app.MapOpenApi();
// app.MapScalarApiReference(options =>
// {
//     options.WithTitle("POS API Documentation")
//            .WithTheme(ScalarTheme.Mars);
// });

app.MapControllers();

app.Run();
