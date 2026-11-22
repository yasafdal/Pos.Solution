using CurrieTechnologies.Razor.SweetAlert2;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using POS.Shared.Interfaces;
using POS.Shared.Services;
using POS.Client.Services;
using POS.Client.Workers;
using System.Linq;
using System.Reflection;

namespace POS.Client
{
    public static class MauiProgram
    {
        public const string DefaultProductionApiBaseUrl = "https://agres.home.arpa/";

        public static string ResolveReleaseApiBaseUrl()
        {
            var configured = typeof(MauiProgram).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "PosApiBaseUrl", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            var url = string.IsNullOrWhiteSpace(configured) ? DefaultProductionApiBaseUrl : configured.Trim();
            return url.EndsWith("/") ? url : url + "/";
        }

        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            // Configure the HttpClient to point to API (configurable via Preferences in DEBUG, centralized via PosApiBaseUrl in RELEASE)
            builder.Services.AddSingleton<PosSessionService>();
            builder.Services.AddScoped(sp =>
            {
#if DEBUG
                var defaultUrl = "http://localhost:5032/";
                var configuredUrl = Microsoft.Maui.Storage.Preferences.Default.Get("UAT_ApiBaseUrl", defaultUrl);
#else
                var defaultUrl = ResolveReleaseApiBaseUrl();
                var configuredUrl = defaultUrl;
#endif
                if (string.IsNullOrWhiteSpace(configuredUrl)) configuredUrl = defaultUrl;
                if (!configuredUrl.EndsWith("/")) configuredUrl += "/";
                var client = new HttpClient
                {
                    BaseAddress = new Uri(configuredUrl),
                    Timeout = TimeSpan.FromSeconds(30)
                };
                var version = typeof(MauiProgram).Assembly.GetName().Version?.ToString(3) ?? "1.0.6";
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"POS.Client/{version}");
                return client;
            });

            //plugins
            builder.Services.AddSweetAlert2();

            // Offline-first services and background worker (Transient DbContext prevents cross-thread collisions)
            builder.Services.AddTransient<POS.Client.Data.LocalPosDbContext>();
            builder.Services.AddTransient<IReferenceDataSyncService, ReferenceDataSyncService>();
            builder.Services.AddTransient<IOfflineCheckoutService, OfflineCheckoutService>();
            builder.Services.AddTransient<IOfflineTransactionSyncService, OfflineTransactionSyncService>();
            builder.Services.AddTransient<IOfflineSyncCoordinator, OfflineSyncCoordinator>();
            builder.Services.AddScoped<IPosConnectivityService, PosConnectivityService>();
            builder.Services.AddSingleton<IOfflineBackgroundSyncService, OfflineBackgroundSyncManager>();
            builder.Services.AddHostedService(sp => (OfflineBackgroundSyncManager)sp.GetRequiredService<IOfflineBackgroundSyncService>());
            builder.Services.AddScoped<CartService>();
            builder.Services.AddScoped<IUserService, UserService>();
            builder.Services.AddScoped<ITerminalService, TerminalService>();
            builder.Services.AddScoped<IDeviceActivationService, DeviceActivationService>();
            builder.Services.AddScoped<AppUpdateService>();
            ClientLicenseService.StrictEnforcementMode = true;
            builder.Services.AddSingleton<IClientLicenseService, ClientLicenseService>();
            builder.Services.AddScoped<ILicensingApiService, LicensingApiService>();
            builder.Services.AddScoped<ICategoryService, CategoryService>();
            builder.Services.AddScoped<IProductService, ProductService>();
            builder.Services.AddScoped<IModifierService, ModifierService>();
            builder.Services.AddScoped<IOrderService, OrderService>();
            builder.Services.AddScoped<IPaymentService, PaymentService>();
            builder.Services.AddScoped<IReceiptService, ReceiptService>();
            builder.Services.AddScoped<IDiscountService, DiscountService>();
            builder.Services.AddScoped<ClientTransactionService>();
            builder.Services.AddScoped<IClientTransactionHistoryService>(sp => sp.GetRequiredService<ClientTransactionService>());
            builder.Services.AddScoped<ITransactionService>(sp => sp.GetRequiredService<ClientTransactionService>());
            builder.Services.AddScoped<IOfflineVoidService, OfflineVoidService>();
            builder.Services.AddScoped<IHeldSaleService, ClientHeldSaleService>();
            builder.Services.AddSingleton<IEscPosNetworkTransport, EscPosTcpTransport>();
            builder.Services.AddSingleton<EthernetPrinterConnection>();
            builder.Services.AddSingleton<UsbPrinterConnection>();
#if WINDOWS
            builder.Services.AddSingleton<IUsbPrinterTransport, POS.Client.Platforms.Windows.WindowsUsbPrinterTransport>();
            builder.Services.AddSingleton<IEscPosImageRasterizer, POS.Client.Platforms.Windows.WindowsEscPosImageRasterizer>();
            builder.Services.AddSingleton<POS.Client.Platforms.Windows.WindowsReceiptPrinter>();
#elif ANDROID
            builder.Services.AddSingleton<IUsbPrinterTransport, POS.Client.Platforms.Android.AndroidUsbPrinterTransport>();
            builder.Services.AddSingleton<IEscPosImageRasterizer, POS.Client.Platforms.Android.AndroidEscPosImageRasterizer>();
            builder.Services.AddSingleton<POS.Client.Platforms.Android.AndroidReceiptPrinter>();
#else
            builder.Services.AddSingleton<IUsbPrinterTransport, UnsupportedUsbPrinterTransport>();
            builder.Services.AddSingleton<IEscPosImageRasterizer, BasicEscPosImageRasterizer>();
            builder.Services.AddSingleton<UnsupportedReceiptPrinter>();
#endif
            builder.Services.AddScoped<EscPosPrinterService>();
            builder.Services.AddScoped<IPrinterService>(sp => sp.GetRequiredService<EscPosPrinterService>());
            builder.Services.AddScoped<ICashDrawerHardwareService>(sp => sp.GetRequiredService<EscPosPrinterService>());
            builder.Services.AddScoped<IReceiptPrinter>(sp =>
            {
                var printerService = sp.GetRequiredService<IPrinterService>();
                var session = sp.GetRequiredService<PosSessionService>();
#if WINDOWS
                IReceiptPrinter ResolveFallback() => sp.GetRequiredService<POS.Client.Platforms.Windows.WindowsReceiptPrinter>();
#elif ANDROID
                IReceiptPrinter ResolveFallback() => sp.GetRequiredService<POS.Client.Platforms.Android.AndroidReceiptPrinter>();
#else
                IReceiptPrinter ResolveFallback() => sp.GetRequiredService<UnsupportedReceiptPrinter>();
#endif
                return new UnifiedReceiptPrinter(printerService, session, ResolveFallback);
            });
#if DEBUG
            // Development only: match Terminals.DeviceIdentifier while the API stays on localhost.
            // Set to null to restore OS route detection. Never included in Release builds.
            const string? developmentTerminalIp = "192.168.1.6";
            builder.Services.AddScoped<ITerminalAddressProvider>(sp =>
                new TerminalAddressProvider(sp.GetRequiredService<HttpClient>())
                {
                    DevelopmentAddressOverride = developmentTerminalIp
                });
#else
            builder.Services.AddScoped<ITerminalAddressProvider, TerminalAddressProvider>();
#endif

            using (var startupDb = new POS.Client.Data.LocalPosDbContext())
            {
                startupDb.InitializeDatabase();
            }

            return builder.Build();
        }

        internal static async ValueTask NotifySplashReadyAsync(Microsoft.JSInterop.IJSRuntime js)
        {
            try
            {
                await js.InvokeVoidAsync("posSplash.markReady");
            }
            catch
            {
                // Ignore if splash overlay is already dismissed or WebView JS runtime is unavailable.
            }
        }
    }
}
