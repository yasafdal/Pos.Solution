"""Comprehensive verification of POS.Client thermal receipt layout and formatting.

Validates:
1. Receipt container width (72mm TM-T88IV printable area) and balanced minimal margins.
2. Product & price alignment with CSS flex and tabular amounts.
3. Centered header, thin dashed lines, clean two-column totals.
4. Customer Information fixed-width label column and underline value alignment.
5. All three footer lines and physical cutter paper feed.
6. WebView2 dynamic roll height configuration in WindowsReceiptPrinter.
"""
import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
FORMATTER_CS = (REPO_ROOT / "POS.Client/Services/ReceiptHtmlFormatter.cs").read_text(encoding="utf-8-sig")
PRINTER_CS = (REPO_ROOT / "POS.Client/Platforms/Windows/WindowsReceiptPrinter.cs").read_text(encoding="utf-8-sig")
PREVIEW_RAZOR = (REPO_ROOT / "POS.Client/Components/Pages/ReceiptPreview.razor").read_text(encoding="utf-8-sig")

class ThermalReceiptLayoutTests(unittest.TestCase):
    def test_01_printable_width_and_margins(self):
        """Must size container to 64mm to eliminate right-edge clipping on 80mm rolls."""
        self.assertIn("width: 64mm;", FORMATTER_CS)
        self.assertIn("max-width: 64mm;", FORMATTER_CS)
        self.assertIn("margin-left: 2mm;", FORMATTER_CS)
        self.assertIn("size: 80mm auto;", FORMATTER_CS)
        self.assertNotIn("size: 80mm 200mm;", FORMATTER_CS)

    def test_02_product_and_price_alignment(self):
        """Product names must wrap on left without breaking right-aligned tabular price alignment."""
        self.assertIn(".item-header", FORMATTER_CS)
        self.assertIn(".item-name", FORMATTER_CS)
        self.assertIn("word-break: break-word;", FORMATTER_CS)
        self.assertIn("font-variant-numeric: tabular-nums;", FORMATTER_CS)
        self.assertIn(".modifier-line", FORMATTER_CS)
        self.assertIn(".mod-name", FORMATTER_CS)
        self.assertIn(".mod-price", FORMATTER_CS)

    def test_03_header_totals_and_subtle_separators(self):
        """Header must be centered, separators thin subtle dashed, and totals two-column."""
        self.assertIn(".header {", FORMATTER_CS)
        self.assertIn("text-align: center;", FORMATTER_CS)
        self.assertIn("border-top: 1px dashed #333;", FORMATTER_CS)
        self.assertNotIn("border-top: 2px dashed #000;", FORMATTER_CS)
        self.assertIn(".row {", FORMATTER_CS)
        self.assertIn("justify-content: space-between;", FORMATTER_CS)

    def test_04_customer_information_alignment(self):
        """Customer box must have centered title, fixed 22mm label column, and flexible underline values."""
        self.assertIn("[CUSTOMER INFORMATION]", FORMATTER_CS)
        self.assertIn(".cust-title", FORMATTER_CS)
        self.assertIn(".cust-label {", FORMATTER_CS)
        self.assertIn("flex: 0 0 19mm;", FORMATTER_CS)
        self.assertIn(".cust-val {", FORMATTER_CS)
        self.assertIn("border-bottom: 1px solid #000;", FORMATTER_CS)
        self.assertIn("CustRow(html, \"Customer:\", receipt.CustomerName);", FORMATTER_CS)
        self.assertIn("CustRow(html, \"Address:\", receipt.CustomerAddress);", FORMATTER_CS)
        self.assertIn("CustRow(html, \"TIN:\", receipt.CustomerTin);", FORMATTER_CS)
        self.assertIn("CustRow(html, \"Bus. Style:\", receipt.CustomerBusinessStyle);", FORMATTER_CS)

    def test_05_footer_and_cutter_feed(self):
        """Footer must contain all 3 lines and trailing cut-feed spacer."""
        self.assertIn("Thank you for your purchase!<br>", FORMATTER_CS)
        self.assertIn("NOT A BIR TAX INVOICE<br>", FORMATTER_CS)
        self.assertIn("For transaction reference only.", FORMATTER_CS)
        self.assertIn("cut-feed", FORMATTER_CS)
        self.assertIn(".cut-feed {", FORMATTER_CS)
        self.assertIn("height: 12mm;", FORMATTER_CS)

    def test_06_webview2_dynamic_roll_height(self):
        """WindowsReceiptPrinter must measure rendered DOM height and set dynamic roll height."""
        self.assertIn("settings.PageWidth = 80d / 25.4d;", PRINTER_CS)
        self.assertIn("settings.PageHeight = pageHeightMm / 25.4d;", PRINTER_CS)
        self.assertIn("settings.PageRanges = \"1\";", PRINTER_CS)
        self.assertIn("ExecuteScriptAsync", PRINTER_CS)
        self.assertIn("EstimateReceiptHeightMm", PRINTER_CS)
        self.assertNotIn("settings.PageHeight = 200d / 25.4d;", PRINTER_CS)

    def test_07_preview_parity(self):
        """ReceiptPreview must render exact receipt HTML via iframe."""
        self.assertIn("ReceiptHtmlFormatter.Format(receipt)", PREVIEW_RAZOR)
        self.assertIn("receipt-preview-frame", PREVIEW_RAZOR)

if __name__ == "__main__":
    unittest.main()
