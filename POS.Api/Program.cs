using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using POS.Api.Models;
using Scalar.AspNetCore;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            Error = "Too many requests. Please wait before retrying.",
            RetryAfterSeconds = 60
        }, token);
    };

    options.AddPolicy("auth-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(POS.Shared.Constants.PosPermissions.AgresSystemAdminPolicy, policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => POS.Shared.Constants.PosPermissions.IsAgresSystemAdmin(ctx.User)));

    options.AddPolicy(POS.Shared.Constants.PosPermissions.ClientAdminPolicy, policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => POS.Shared.Constants.PosPermissions.IsClientAdmin(ctx.User)));

    options.AddPolicy(POS.Shared.Constants.PosPermissions.ClientUserManagementPolicy, policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => POS.Shared.Constants.PosPermissions.IsClientUserManager(ctx.User)));

    options.AddPolicy(POS.Shared.Constants.PosPermissions.ClientOperationsPolicy, policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => POS.Shared.Constants.PosPermissions.IsClientUser(ctx.User)));

    options.AddPolicy(POS.Shared.Constants.PosPermissions.TransactionHistoryPolicy, policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => POS.Shared.Constants.PosPermissions.CanViewTransactionHistory(ctx.User)));
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
builder.Services.AddScoped<POS.Api.Services.ILicensingService, POS.Api.Services.LicensingService>();

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
else
{
    try
    {
        var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connStr))
        {
            using var conn = new Npgsql.NpgsqlConnection(connStr);
            conn.Open();
            using var cmd = new Npgsql.NpgsqlCommand(@"
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""ReceiptLogoUrl"" VARCHAR(255) NULL;
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""ReceiptLogoUpdatedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""VatDisplay"" BOOLEAN NOT NULL DEFAULT TRUE;
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""BranchTagline"" VARCHAR(200) NULL;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ServerReceivedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NULL;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""DeviceTimeZoneId"" VARCHAR(100) NULL;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""DeviceUtcOffsetMinutes"" INT NULL;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ClockSkewSeconds"" INT NULL;
                ALTER TABLE ""HeldSales"" ADD COLUMN IF NOT EXISTS ""BusinessDate"" VARCHAR(10) NULL;
                ALTER TABLE ""HeldSales"" ADD COLUMN IF NOT EXISTS ""CancelledAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
                ALTER TABLE ""HeldSales"" ADD COLUMN IF NOT EXISTS ""CancellationReason"" VARCHAR(255) NULL;
                ALTER TABLE ""TransactionVoids"" ADD COLUMN IF NOT EXISTS ""ClientVoidId"" VARCHAR(100) NULL;
                UPDATE ""Payments"" p
                SET ""paid_at"" = p.""paid_at"" + (o.""DeviceUtcOffsetMinutes"" * INTERVAL '1 minute')
                FROM ""Orders"" o
                WHERE p.""order_id"" = o.""order_id""
                  AND o.""DeviceUtcOffsetMinutes"" IS NOT NULL
                  AND ABS(o.""DeviceUtcOffsetMinutes"") >= 180
                  AND o.""ServerReceivedAtUtc"" IS NOT NULL
                  AND ABS(EXTRACT(EPOCH FROM (o.""created_at"" - o.""ServerReceivedAtUtc""))) < 7200;
                UPDATE ""Orders""
                SET ""created_at"" = ""created_at"" + (""DeviceUtcOffsetMinutes"" * INTERVAL '1 minute'),
                    ""invoice_issued_at"" = CASE WHEN ""invoice_issued_at"" IS NOT NULL THEN ""invoice_issued_at"" + (""DeviceUtcOffsetMinutes"" * INTERVAL '1 minute') ELSE NULL END
                WHERE ""DeviceUtcOffsetMinutes"" IS NOT NULL
                  AND ABS(""DeviceUtcOffsetMinutes"") >= 180
                  AND ""ServerReceivedAtUtc"" IS NOT NULL
                  AND ABS(EXTRACT(EPOCH FROM (""created_at"" - ""ServerReceivedAtUtc""))) < 7200;
            ", conn);
            cmd.ExecuteNonQuery();
            POS.Api.MigrationRunner.ApplyLicensingSchema(conn);
            POS.Api.MigrationRunner.ApplyAgresIdentitySchema(conn);
            POS.Api.MigrationRunner.ApplyDiscountSyncSchema(conn);
        }
    }
    catch { /* Non-fatal if DB is unreachable at startup */ }
}

// Security headers middleware
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    await next();
});

var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
POS.Api.Services.ReceiptLogoStorage.ConfigureWebRoot(webRootPath, app.Environment.ContentRootPath);
try
{
    var uploadsDir = Path.Combine(webRootPath, "uploads", "products");
    if (!Directory.Exists(uploadsDir))
    {
        Directory.CreateDirectory(uploadsDir);
    }
    var logosDir = Path.Combine(webRootPath, "uploads", "logos");
    if (!Directory.Exists(logosDir))
    {
        Directory.CreateDirectory(logosDir);
    }
}
catch { /* Non-fatal if folder already managed by host */ }

// app.UseRouting();
app.UseStaticFiles();
app.UseCors("AllowAll"); // Enable CORS
app.UseRateLimiter();

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
