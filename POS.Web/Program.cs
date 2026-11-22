using CurrieTechnologies.Razor.SweetAlert2;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using POS.Shared.Models;
using POS.Shared.Interfaces;
using POS.Shared.Services;
using POS.Web.Components;
using POS.Web.Components.Account;
using POS.Web.Data;
using POS.Web.Auth;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    var configuredProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();
    if (configuredProxies != null)
    {
        foreach (var proxy in configuredProxies)
        {
            if (IPAddress.TryParse(proxy?.Trim(), out var ip) && !options.KnownProxies.Contains(ip))
            {
                options.KnownProxies.Add(ip);
            }
        }
    }
});

var dataProtectionKeyDir = builder.Configuration["DataProtection:KeyDirectory"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeyDir))
{
    Directory.CreateDirectory(dataProtectionKeyDir);
    builder.Services.AddDataProtection()
        .SetApplicationName("POS.Web")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyDir));
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAuthorizationCore(options =>
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
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<CustomAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());


builder.Services.AddScoped<IdentityRedirectManager>();


builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
bool hasIdentityDb = !string.IsNullOrWhiteSpace(connectionString);
if (hasIdentityDb)
{
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseSqlServer(connectionString));
    builder.Services.AddDatabaseDeveloperPageExceptionFilter();

    builder.Services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = true;
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();
}

builder.Services.AddScoped(_ =>
{
    var handler = new HttpClientHandler();

    if (builder.Environment.IsDevelopment())
    {
        // Ignore self-signed SSL certificate errors in Development only.
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }

    var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
        ?? builder.Configuration["ApiSettings:BaseUrl"]
        ?? throw new InvalidOperationException("ApiBaseUrl is missing.");
    if (!apiBaseUrl.EndsWith('/')) apiBaseUrl += "/";

    return new HttpClient(handler)
    {
        BaseAddress = new Uri(apiBaseUrl)
    };
});

//extensions
builder.Services.AddSweetAlert2();



//Models
if (hasIdentityDb)
{
    builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
}
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<ITaxService, TaxService>();
builder.Services.AddScoped<IBranchService, BranchService>();
builder.Services.AddScoped<IOutletService, OutletService>();
builder.Services.AddScoped<ITerminalService, TerminalService>();
builder.Services.AddScoped<ILicensingApiService, LicensingApiService>();
builder.Services.AddScoped<IAgresPlatformService, AgresPlatformService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IUomService, UomService>();
builder.Services.AddScoped<IServiceModeService, ServiceModeService>();
builder.Services.AddScoped<IBusinessTypeService, BusinessTypeService>();
builder.Services.AddScoped<IProductRecipeService, ProductRecipeService>();
builder.Services.AddScoped<IModifierService, ModifierService>();
builder.Services.AddScoped<IHeldSaleService, HeldSaleService>();
builder.Services.AddScoped<ISalesReportService, SalesReportApiService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IDiscountService, DiscountService>();
builder.Services.AddScoped<IZReadingAuditService, ZReadingAuditService>();
builder.Services.AddSingleton<POS.Web.Services.ProductImageUrlResolver>();

var app = builder.Build();

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    if (hasIdentityDb)
    {
        app.UseMigrationsEndPoint();
    }
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // Users-table authentication is restored inside the interactive circuit from
    // protected session storage; AuthorizeRouteView enforces page authorization.
    .AllowAnonymous();

// Add additional endpoints required by the Identity /Account Razor components only when Identity DB is configured.
if (hasIdentityDb)
{
    app.MapAdditionalIdentityEndpoints();
}

app.Run();
