using Microsoft.Maui.ApplicationModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using POS.Client.Services;
using POS.Shared.Models;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace POS.Client.Platforms.Windows;

/// <summary>
/// Direct WebView2 printing to the EPSON queue using a hidden receipt-only host.
/// The cashier's BlazorWebView, navigation, and cart are never replaced.
/// </summary>
public sealed class WindowsReceiptPrinter : IReceiptPrinter
{
    // Windows queue name, not the ReceiptE4 driver name. Never use the default printer.
    private const string PrinterName = "EPSON TM-T88IV Receipt";
    private int _printing;

    public async Task PrintAsync(ReceiptDto receipt)
    {
        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A receipt is already being sent to the printer. Wait before printing again.");

        try
        {
            var html = ReceiptHtmlFormatter.Format(receipt);
            await MainThread.InvokeOnMainThreadAsync(() => PrintDirectAsync(html));
        }
        finally { Interlocked.Exchange(ref _printing, 0); }
    }

    public Task<string> SavePdfAsync(ReceiptDto receipt)
    {
        var html = ReceiptHtmlFormatter.Format(receipt);
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var receiptsDir = Path.Combine(documentsPath, "POS_Receipts");
            Directory.CreateDirectory(receiptsDir);

            var safeOrderNumber = string.Join("_", receipt.OrderNumber.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(receiptsDir, $"Receipt_{safeOrderNumber}.pdf");

            var webView = new WebView2();
            var window = new NativeWindow { Title = "Generating Receipt PDF..." };
            var layout = new Microsoft.UI.Xaml.Controls.Grid();
            layout.Children.Add(webView);
            window.Content = layout;

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => loaded.TrySetException(new InvalidOperationException("PDF generation window closed."));

            window.Activate();
            try
            {
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                    Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "ReceiptWebView2"), null);
                await webView.EnsureCoreWebView2Async(environment);

                var core = webView.CoreWebView2;
                core.Settings.IsScriptEnabled = false;
                core.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess) loaded.TrySetResult();
                    else loaded.TrySetException(new InvalidOperationException("Receipt render failed: " + args.WebErrorStatus));
                };
                core.NavigateToString(html);
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));

                var settings = core.Environment.CreatePrintSettings();
                settings.PageWidth = 80d / 25.4d;
                settings.PageHeight = 297d / 25.4d;
                settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0;
                settings.ShouldPrintHeaderAndFooter = false;
                settings.ShouldPrintBackgrounds = true;

                var written = await core.PrintToPdfAsync(filePath, settings);
                window.Close();

                if (!written || !File.Exists(filePath))
                    throw new IOException("WebView2 did not create the PDF file.");

                return filePath;
            }
            catch
            {
                try { window.Close(); } catch { }
                throw;
            }
        });
    }

    private static async Task PrintDirectAsync(string html)
    {
        var webView = new WebView2();
        var window = new NativeWindow { Title = "Receipt printing" };
        var layout = new Microsoft.UI.Xaml.Controls.Grid();
        layout.Children.Add(webView);
        window.Content = layout;
        // Initialize a native host without activating a preview or taking cashier focus.
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "ReceiptWebView2"), null);
            await webView.EnsureCoreWebView2Async(environment);
            var core = webView.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                if (!ReceiptNavigationPolicy.IsAllowed(args.Uri)) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) loaded.TrySetResult();
                else loaded.TrySetException(new InvalidOperationException("Receipt render failed: " + args.WebErrorStatus));
            };
            core.NavigateToString(html);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Matches the installed EPSON driver's 80 x 297 mm portrait form.
            var settings = core.Environment.CreatePrintSettings();
            settings.PrinterName = PrinterName;
            settings.MediaSize = CoreWebView2PrintMediaSize.Custom;
            settings.PageWidth = 80d / 25.4d;
            settings.PageHeight = 297d / 25.4d;
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0;
            settings.ScaleFactor = 1;
            settings.Copies = 1;
            settings.PagesPerSide = 1;
            settings.ColorMode = CoreWebView2PrintColorMode.Grayscale;
            settings.Duplex = CoreWebView2PrintDuplex.OneSided;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintSelectionOnly = false;

            // No timeout or automatic retry after submission: an ambiguous result can print duplicates.
            var result = await core.PrintAsync(settings);
            if (result == CoreWebView2PrintStatus.PrinterUnavailable)
                throw new InvalidOperationException($"Printer '{PrinterName}' is unavailable. Check its connection and Windows print queue.");
            if (result != CoreWebView2PrintStatus.Succeeded)
                throw new InvalidOperationException($"Could not submit the receipt to '{PrinterName}'. Check the Windows print queue before retrying to avoid duplicates.");
        }
        finally
        {
            try { webView.Close(); }
            finally { window.Close(); }
        }
    }
}
