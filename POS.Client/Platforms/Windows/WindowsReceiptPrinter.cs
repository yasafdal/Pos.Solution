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

    public async Task PrintXReadingAsync(XReadingReportDto report, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A receipt or report is already being sent to the printer. Wait before printing again.");

        try
        {
            var html = XReadingHtmlFormatter.Format(report);
            await MainThread.InvokeOnMainThreadAsync(() => PrintDirectAsync(html, printerName, "Print X-Reading Report"));
        }
        finally { Interlocked.Exchange(ref _printing, 0); }
    }

    public async Task PrintCashierDeclarationAsync(CashierDeclarationReportDto declaration, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A receipt or acknowledgment is already being sent to the printer. Wait before printing again.");

        try
        {
            var html = CashierDeclarationHtmlFormatter.Format(declaration);
            await MainThread.InvokeOnMainThreadAsync(() => PrintDirectAsync(html, printerName, "Print Cashier Declaration Acknowledgment"));
        }
        finally { Interlocked.Exchange(ref _printing, 0); }
    }

    public async Task<bool> PrintZReadingAsync(ZReadingReportDto report, string? printerName = null, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A report is already being sent to the printer. Wait before retrying.");
        try
        {
            var html = ZReadingHtmlFormatter.Format(report, isReprint);
            return await MainThread.InvokeOnMainThreadAsync(() => PrintDirectAsync(html, printerName, "Print Z-Reading Report"));
        }
        finally { Interlocked.Exchange(ref _printing, 0); }
    }

    private static async Task<bool> PrintDirectAsync(string html, string? targetPrinter = null, string windowTitle = "Print Receipt")
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

            // Dynamic roll page height measurement: fits entire receipt without clipping customer info or footer
            double measuredHeightMm = 0;
            try
            {
                var rawHeight = await core.ExecuteScriptAsync(
                    "Math.max(document.body.scrollHeight, document.documentElement.scrollHeight, document.body.offsetHeight, document.documentElement.offsetHeight)"
                );
                if (!string.IsNullOrWhiteSpace(rawHeight) && double.TryParse(rawHeight, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pxHeight) && pxHeight > 0)
                {
                    measuredHeightMm = (pxHeight * 25.4d) / 96d;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to measure receipt height in WebView2: {ex.Message}");
            }

            double pageHeightMm = measuredHeightMm > 0 ? (measuredHeightMm + 4d) : Math.Max(180d, EstimateReceiptHeightMm(html));

            // Configure custom 80 mm roll paper with dynamic content height to print all sections before cutting
            CoreWebView2PrintStatus status = CoreWebView2PrintStatus.OtherError;
            try
            {
                var settings = core.Environment.CreatePrintSettings();
                settings.MediaSize = CoreWebView2PrintMediaSize.Custom;
                settings.PageWidth = 80d / 25.4d;
                settings.PageHeight = pageHeightMm / 25.4d;
                settings.MarginTop = 0;
                settings.MarginBottom = 0;
                settings.MarginLeft = 0;
                settings.MarginRight = 0;
                settings.ShouldPrintHeaderAndFooter = false;
                settings.PageRanges = "1";
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
                return true;
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
            // Dialog close/cancel has no reliable print acknowledgement.
            return false;
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

    private static double EstimateReceiptHeightMm(string html)
    {
        double height = 150d;
        int itemCount = CountOccurrences(html, "class=\"item-block\"");
        height += itemCount * 12d;
        int modCount = CountOccurrences(html, "class=\"modifier-line\"");
        height += modCount * 5d;
        int payCount = CountOccurrences(html, "Payment:");
        height += payCount * 8d;
        return height;
    }

    private static int CountOccurrences(string source, string match)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(match)) return 0;
        int count = 0, idx = 0;
        while ((idx = source.IndexOf(match, idx, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            idx += match.Length;
        }
        return count;
    }
}
