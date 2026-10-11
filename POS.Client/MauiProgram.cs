using CurrieTechnologies.Razor.SweetAlert2;
using Microsoft.Extensions.Logging;
using POS.Shared.Interfaces;
using POS.Shared.Services;
using POS.Client.Services;

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
            builder.Services.AddScoped(sp => new HttpClient
            {
                BaseAddress = new Uri("https://localhost:7139/")
            });

            //plugins
            builder.Services.AddSweetAlert2();

            // Services
            builder.Services.AddSingleton<PosSessionService>();
            builder.Services.AddScoped<CartService>();
            builder.Services.AddScoped<IUserService, UserService>();
            builder.Services.AddScoped<IOutletService, OutletService>();
            builder.Services.AddScoped<ICategoryService, CategoryService>();
            builder.Services.AddScoped<IProductService, ProductService>();
#if DEBUG
            // Development only: match Outlets.DeviceIdentifier while the API stays on localhost.
            // Set to null to restore OS route detection. Never included in Release builds.
            const string? developmentTerminalIp = "192.168.1.8";
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
