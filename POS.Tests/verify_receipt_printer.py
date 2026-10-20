"""Source-level regression checks for the Windows receipt WebView2 lifecycle.

Run with: python POS.Tests/verify_receipt_printer.py
This checks lifecycle ordering; it does not validate printer hardware.
"""
from pathlib import Path
import unittest


SOURCE = (
    Path(__file__).resolve().parents[1]
    / "POS.Client/Platforms/Windows/WindowsReceiptPrinter.cs"
).read_text(encoding="utf-8-sig")


class ReceiptPrinterLifecycleTests(unittest.TestCase):
    def test_print_window_is_activated_before_webview_initialization(self):
        method = SOURCE.split("private static async Task PrintDirectAsync", 1)[1]
        initialization = method.index("await webView.EnsureCoreWebView2Async")
        self.assertIn("window.Activate();", method[:initialization])

    def test_receipt_browsers_use_the_same_defaults_as_the_main_winui_browser(self):
        self.assertNotIn("CoreWebView2Environment.CreateWithOptionsAsync", SOURCE)
        self.assertEqual(1, SOURCE.count("await webView.EnsureCoreWebView2Async();"))

    def test_initialization_failure_is_captured_for_print(self):
        self.assertEqual(1, SOURCE.count("webView.CoreWebView2Initialized +="))
        self.assertEqual(1, SOURCE.count("initializationError = args.Exception"))
        self.assertEqual(1, SOURCE.count("initializationError.HResult:X8"))

    def test_pdf_export_is_removed(self):
        root = Path(__file__).resolve().parents[1] / "POS.Client"
        for relative in ["Components/Pages/ReceiptPreview.razor", "Services/IReceiptPrinter.cs", "Services/UnsupportedReceiptPrinter.cs", "Platforms/Windows/WindowsReceiptPrinter.cs"]:
            text = (root / relative).read_text(encoding="utf-8-sig")
            self.assertNotIn("SavePdf", text, relative)
            self.assertNotIn("savingPdf", text, relative)
            self.assertNotIn("savedPdfPath", text, relative)

    def test_receipt_paper_is_80_by_200_mm(self):
        self.assertIn("settings.PageWidth = 80d / 25.4d;", SOURCE)
        self.assertIn("settings.PageHeight = 200d / 25.4d;", SOURCE)
        self.assertIn("settings.MediaSize = CoreWebView2PrintMediaSize.Custom;", SOURCE)
        self.assertIn("core.PrintAsync(settings)", SOURCE)
        formatter = (Path(__file__).resolve().parents[1] / "POS.Client/Services/ReceiptHtmlFormatter.cs").read_text()
        self.assertIn("size: 80mm 200mm", formatter)

    def test_print_rejects_an_uninitialized_core_explicitly(self):
        assignments = SOURCE.split("var core = webView.CoreWebView2")[1:]
        self.assertEqual(1, len(assignments))
        for assignment in assignments:
            self.assertTrue(assignment.lstrip().startswith("?? throw new InvalidOperationException("))


if __name__ == "__main__":
    unittest.main()
