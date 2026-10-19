using CurrieTechnologies.Razor.SweetAlert2;
using Microsoft.Extensions.Logging;
using POS.Shared.Interfaces;
using POS.Shared.Services;
using POS.Client.Services;
using POS.Client.Workers;

namespace POS.Client
{
    public static class MauiProgram
    {
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

            //Configure the HttpClient to point to your running API (Update the port to match yours)
            builder.Services.AddSingleton<PosSessionService>();
            builder.Services.AddScoped(sp => new HttpClient
            {
                BaseAddress = new Uri("http://localhost:5032/")
            });

            //plugins
            builder.Services.AddSweetAlert2();

            // Offline-first services and background worker (Transient DbContext prevents cross-thread collisions)
            builder.Services.AddTransient<POS.Client.Data.LocalPosDbContext>();
            builder.Services.AddTransient<IReferenceDataSyncService, ReferenceDataSyncService>();
            builder.Services.AddTransient<IOfflineCheckoutService, OfflineCheckoutService>();
            builder.Services.AddTransient<IOfflineTransactionSyncService, OfflineTransactionSyncService>();
            builder.Services.AddTransient<IOfflineSyncCoordinator, OfflineSyncCoordinator>();
            builder.Services.AddSingleton<IPosConnectivityService, PosConnectivityService>();
            builder.Services.AddSingleton<IOfflineBackgroundSyncService, OfflineBackgroundSyncManager>();
            builder.Services.AddHostedService(sp => (OfflineBackgroundSyncManager)sp.GetRequiredService<IOfflineBackgroundSyncService>());
            builder.Services.AddScoped<CartService>();
            builder.Services.AddScoped<IUserService, UserService>();
            builder.Services.AddScoped<IOutletService, OutletService>();
            builder.Services.AddScoped<ICategoryService, CategoryService>();
            builder.Services.AddScoped<IProductService, ProductService>();
            builder.Services.AddScoped<IModifierService, ModifierService>();
            builder.Services.AddScoped<IOrderService, OrderService>();
            builder.Services.AddScoped<IPaymentService, PaymentService>();
            builder.Services.AddScoped<IReceiptService, ReceiptService>();
            builder.Services.AddScoped<IDiscountService, DiscountService>();
            builder.Services.AddScoped<ITransactionService, TransactionService>();
            builder.Services.AddScoped<IHeldSaleService, ClientHeldSaleService>();
#if WINDOWS
            builder.Services.AddSingleton<IReceiptPrinter, POS.Client.Platforms.Windows.WindowsReceiptPrinter>();
#else
            builder.Services.AddSingleton<IReceiptPrinter, UnsupportedReceiptPrinter>();
#endif
#if DEBUG
            // Development only: match Outlets.DeviceIdentifier while the API stays on localhost.
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

            return builder.Build();
        }
    }
}
