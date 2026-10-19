using Microsoft.Maui.ApplicationModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using POS.Client.Services;
using POS.Shared.Models;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace POS.Client.Platforms.Windows;

/// <summary>
/// Printer-agnostic WebView2 receipt printer using the OS default printer or print dialog fallback.
/// Does not rely on any hardcoded printer queue name or driver paper size configuration.
/// </summary>
public sealed class WindowsReceiptPrinter : IReceiptPrinter
{
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

                var written = await core.PrintToPdfAsync(filePath, null);
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
        var window = new NativeWindow { Title = "Print Receipt" };
        var layout = new Microsoft.UI.Xaml.Controls.Grid();
        layout.Children.Add(webView);
        window.Content = layout;

        bool keepWindowOpenForDialog = false;
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

            // First try silent printing to the OS default printer using its native driver configuration (no hardcoded printer name or page size).
            CoreWebView2PrintStatus status = CoreWebView2PrintStatus.OtherError;
            try
            {
                status = await core.PrintAsync(null);
            }
            catch
            {
                status = CoreWebView2PrintStatus.OtherError;
            }

            if (status == CoreWebView2PrintStatus.Succeeded)
            {
                return;
            }

            // Fallback: if no silent default printer is ready or the default printer requires interactive selection (e.g., Print to PDF),
            // show the standard print dialog so the user can choose any available printer and configuration.
            keepWindowOpenForDialog = true;
            var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) =>
            {
                try { webView.Close(); } catch { }
                windowClosed.TrySetResult();
            };

            window.Activate();
            core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            await windowClosed.Task;
        }
        finally
        {
            if (!keepWindowOpenForDialog)
            {
                try { webView.Close(); }
                finally { window.Close(); }
            }
        }
    }
}
