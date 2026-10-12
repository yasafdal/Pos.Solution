using Microsoft.Maui.ApplicationModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using POS.Client.Services;
using POS.Shared.Models;
using Windows.Storage.Pickers;
using NativeWindow = Microsoft.UI.Xaml.Window;
using Button = Microsoft.UI.Xaml.Controls.Button;
using Thickness = Microsoft.UI.Xaml.Thickness;
using RowDefinition = Microsoft.UI.Xaml.Controls.RowDefinition;
using GridLength = Microsoft.UI.Xaml.GridLength;
using GridUnitType = Microsoft.UI.Xaml.GridUnitType;

namespace POS.Client.Platforms.Windows;

/// <summary>
/// Native WebView2 print adapter. Uses a separate receipt-only window so the cashier's
/// BlazorWebView, navigation, and cart are never replaced. No window.print JavaScript.
/// </summary>
public sealed class WindowsReceiptPrinter : IReceiptPrinter
{
    // Register as a singleton: retain the preview until the user explicitly closes it.
    private NativeWindow? _preview;

    public Task PrintAsync(ReceiptDto receipt)
    {
        var html = ReceiptHtmlFormatter.Format(receipt);
        return MainThread.InvokeOnMainThreadAsync(() => OpenPrintPreviewAsync(html));
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

    private async Task OpenPrintPreviewAsync(string html)
    {
        if (_preview is not null)
        {
            _preview.Activate();
            throw new InvalidOperationException("Close the existing receipt preview before printing another receipt.");
        }

        var webView = new WebView2();
        var window = new NativeWindow { Title = "Receipt — Print / Save as PDF" };
        var status = new TextBlock { Text = "Loading receipt…", TextWrapping = TextWrapping.Wrap };
        var print = new Button { Content = "Print…", IsEnabled = false };
        var save = new Button { Content = "Save PDF…", IsEnabled = false };
        var close = new Button { Content = "Close preview" };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(print);
        toolbar.Children.Add(save);
        toolbar.Children.Add(close);
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        header.Children.Add(toolbar);
        header.Children.Add(status);
        var layout = new Microsoft.UI.Xaml.Controls.Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(header);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(webView, 1);
        layout.Children.Add(webView);
        window.Content = layout;
        _preview = window;
        var closed = false;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) =>
        {
            closed = true;
            loaded.TrySetException(new InvalidOperationException("Receipt preview was closed before it was ready."));
            webView.Close();
            if (ReferenceEquals(_preview, window)) _preview = null;
        };
        close.Click += (_, _) => window.Close();
        window.Activate();
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "ReceiptWebView2"), null);
            await webView.EnsureCoreWebView2Async(environment);
            if (closed) throw new InvalidOperationException("Receipt preview was closed.");
            var core = webView.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                // NavigateToString uses about:blank; no receipt data can navigate to a URL.
                if (!string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) loaded.TrySetResult();
                else loaded.TrySetException(new InvalidOperationException("Receipt render failed: " + args.WebErrorStatus));
            };
            core.NavigateToString(html);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            print.IsEnabled = true;
            save.IsEnabled = true;
            print.Click += (_, _) =>
            {
                try { ShowPrintDialog(core, status); }
                catch (Exception ex) { status.Text = "Could not open print dialog: " + ex.Message; }
            };
            save.Click += async (_, _) =>
            {
                save.IsEnabled = false;
                print.IsEnabled = false;
                try
                {
                    var picker = new FileSavePicker { SuggestedFileName = "sales-receipt" };
                    picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                    var file = await picker.PickSaveFileAsync();
                    if (file is null) { status.Text = "PDF save cancelled. Payment is unchanged."; return; }
                    var settings = core.Environment.CreatePrintSettings();
                    settings.PageWidth = 80d / 25.4d;
                    settings.PageHeight = 297d / 25.4d;
                    settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0;
                    settings.ShouldPrintHeaderAndFooter = false;
                    settings.ShouldPrintBackgrounds = true;
                    var written = await core.PrintToPdfAsync(file.Path, settings);
                    if (!written) throw new IOException("WebView2 did not create the PDF.");
                    status.Text = "PDF saved. This does not mean a paper receipt was printed.";
                }
                catch (Exception ex) { status.Text = "PDF save failed: " + ex.Message; }
                finally
                {
                    if (!closed) { save.IsEnabled = true; print.IsEnabled = true; }
                }
            };
            ShowPrintDialog(core, status);
        }
        catch
        {
            if (!closed) window.Close();
            throw;
        }
    }

    private static void ShowPrintDialog(CoreWebView2 core, TextBlock status)
    {
        core.ShowPrintUI(CoreWebView2PrintDialogKind.System);
        status.Text = "Print dialog opened. Select Microsoft Print to PDF or your installed printer. "
            + "Cancellation and physical printing are not confirmed. Close this preview when finished.";
    }
}
