#if ANDROID
using Android.Content;
using Android.Print;
using Android.Webkit;
using Microsoft.Maui.ApplicationModel;
using POS.Client.Services;
using POS.Shared.Models;

namespace POS.Client.Platforms.Android;

/// <summary>
/// Android receipt printer adapter using the native Android PrintManager framework.
/// Compatible with all Android thermal, Bluetooth, Wi-Fi, USB OTG, and system print services.
/// </summary>
public sealed class AndroidReceiptPrinter : IReceiptPrinter
{
    private int _printing;

    public async Task PrintAsync(ReceiptDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A receipt is already being sent to the printer. Please wait.");

        try
        {
            var html = ReceiptHtmlFormatter.Format(receipt);
            await PrintHtmlOnAndroidAsync(html, $"Receipt-{receipt.InvoiceNumber}");
        }
        finally
        {
            Interlocked.Exchange(ref _printing, 0);
        }
    }

    public async Task PrintPreparationTicketAsync(PreparationTicketDto ticket, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A ticket is already being sent to the printer. Please wait.");

        try
        {
            var html = PreparationTicketHtmlFormatter.Format(ticket);
            await PrintHtmlOnAndroidAsync(html, $"{ticket.Title}-{ticket.OrderNumber}");
        }
        finally
        {
            Interlocked.Exchange(ref _printing, 0);
        }
    }

    public async Task PrintXReadingAsync(XReadingReportDto report, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A report is already being sent to the printer. Please wait.");

        try
        {
            var html = XReadingHtmlFormatter.Format(report);
            await PrintHtmlOnAndroidAsync(html, "X-Reading Report");
        }
        finally
        {
            Interlocked.Exchange(ref _printing, 0);
        }
    }

    public async Task<bool> PrintZReadingAsync(ZReadingReportDto report, string? printerName = null, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("A report is already being sent to the printer. Please wait.");

        try
        {
            var html = ZReadingHtmlFormatter.Format(report, isReprint);
            await PrintHtmlOnAndroidAsync(html, "Z-Reading Report");
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _printing, 0);
        }
    }

    public async Task PrintCashierDeclarationAsync(CashierDeclarationReportDto declaration, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (Interlocked.CompareExchange(ref _printing, 1, 0) != 0)
            throw new InvalidOperationException("An acknowledgment is already being sent to the printer. Please wait.");

        try
        {
            var html = CashierDeclarationHtmlFormatter.Format(declaration);
            await PrintHtmlOnAndroidAsync(html, "Cashier Declaration");
        }
        finally
        {
            Interlocked.Exchange(ref _printing, 0);
        }
    }

    private static Task PrintHtmlOnAndroidAsync(string html, string jobName)
    {
        var tcs = new TaskCompletionSource<bool>();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity == null)
                {
                    tcs.TrySetException(new InvalidOperationException("Android CurrentActivity is not ready. Ensure the application is in foreground."));
                    return;
                }

                var webView = new global::Android.Webkit.WebView(activity);
                webView.SetWebViewClient(new PrintWebViewClient(activity, jobName, tcs));
                webView.LoadDataWithBaseURL("file:///android_asset/", html, "text/html", "utf-8", null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private sealed class PrintWebViewClient : WebViewClient
    {
        private readonly global::Android.App.Activity _activity;
        private readonly string _jobName;
        private readonly TaskCompletionSource<bool> _tcs;
        private bool _printed;

        public PrintWebViewClient(global::Android.App.Activity activity, string jobName, TaskCompletionSource<bool> tcs)
        {
            _activity = activity;
            _jobName = jobName;
            _tcs = tcs;
        }

        public override void OnPageFinished(global::Android.Webkit.WebView? view, string? url)
        {
            if (_printed || view == null) return;
            _printed = true;

            try
            {
                var printManager = (PrintManager?)_activity.GetSystemService(Context.PrintService);
                if (printManager == null)
                {
                    _tcs.TrySetException(new InvalidOperationException("Android PrintManager service is not available on this device."));
                    return;
                }

                var adapter = view.CreatePrintDocumentAdapter(_jobName);
                var attributes = new PrintAttributes.Builder().Build();

                printManager.Print(_jobName, adapter, attributes);
                _tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}
#endif
