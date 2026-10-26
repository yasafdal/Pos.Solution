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
            throw new InvalidOperationException("A receipt or ticket is already being sent to the printer. Wait before printing again.");

        try
        {
            var html = ReceiptHtmlFormatter.Format(receipt);
            await MainThread.InvokeOnMainThreadAsync(() => PrintDirectAsync(html, null, "Print Receipt"));
        }
        finally { Interlocked.Exchange(ref _printing, 0); }
    }

    public async Task PrintPreparationTicketAsync(PreparationTicketDto ticket, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A receipt or ticket is already being sent to the printer. Wait before printing again.");

        try
        {
            var html = PreparationTicketHtmlFormatter.Format(ticket);
            var targetPrinter = !string.IsNullOrWhiteSpace(printerName) ? printerName : ticket.AssignedPrinterName;
            await MainThread.InvokeOnMainThreadAsync(() => PrintDirectAsync(html, targetPrinter, ticket.Title));
        }
        finally { Interlocked.Exchange(ref _printing, 0); }
    }

    private static async Task PrintDirectAsync(string html, string? targetPrinter = null, string windowTitle = "Print Receipt")
    {
        var webView = new WebView2();
        var window = new NativeWindow { Title = windowTitle };
        var layout = new Microsoft.UI.Xaml.Controls.Grid();
        layout.Children.Add(webView);
        window.Content = layout;

        bool keepWindowOpenForDialog = false;
        try
        {
            // Attach the receipt browser to its native window before initialization.
            window.Activate();
            // Match the main WinUI browser's options because the process overrides the profile folder.
            Exception? initializationError = null;
            webView.CoreWebView2Initialized += (_, args) => initializationError = args.Exception;
            await webView.EnsureCoreWebView2Async();
            if (initializationError != null)
                throw new InvalidOperationException($"Receipt browser initialization failed (0x{initializationError.HResult:X8}): {initializationError.Message}", initializationError);
            var core = webView.CoreWebView2
                ?? throw new InvalidOperationException("The receipt browser did not initialize. Close the print window and retry.");
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

            // First try the OS default printer with 80 x 200 mm receipt paper; the driver must support this medium.
            CoreWebView2PrintStatus status = CoreWebView2PrintStatus.OtherError;
            try
            {
                var settings = core.Environment.CreatePrintSettings();
                settings.MediaSize = CoreWebView2PrintMediaSize.Custom;
                settings.PageWidth = 80d / 25.4d;
                // shortcut: fixed 200 mm pages, configure roll length when the physical printer is selected.
                settings.PageHeight = 200d / 25.4d;
                settings.MarginTop = 0;
                settings.MarginBottom = 0;
                settings.MarginLeft = 0;
                settings.MarginRight = 0;
                settings.ShouldPrintHeaderAndFooter = false;
                if (!string.IsNullOrWhiteSpace(targetPrinter))
                {
                    settings.PrinterName = targetPrinter;
                }
                status = await core.PrintAsync(settings);
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
        catch(Exception ex)
        {
            // Log the exception or handle it as needed
            Console.WriteLine($"Error occurred while printing receipt: {ex.Message}");
            throw;
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
