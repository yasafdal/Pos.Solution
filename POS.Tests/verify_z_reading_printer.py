"""Static printer-boundary checks; does not invoke a printer or application."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class ZReadingPrinterTests(unittest.TestCase):
    def test_z_print_distinguishes_accepted_from_unknown_dialog(self):
        interface = (ROOT / 'POS.Client/Services/IReceiptPrinter.cs').read_text(encoding='utf-8-sig')
        printer = (ROOT / 'POS.Client/Platforms/Windows/WindowsReceiptPrinter.cs').read_text(encoding='utf-8-sig')
        self.assertIn('Task<bool> PrintZReadingAsync', interface)
        self.assertIn('Task<bool> PrintDirectAsync', printer)
        direct = printer[printer.index('if (status == CoreWebView2PrintStatus.Succeeded)'):]
        self.assertIn('return true;', direct)
        self.assertIn('await windowClosed.Task;', direct)
        self.assertIn('return false;', direct)
        method = printer[printer.index('public async Task<bool> PrintZReadingAsync'):printer.index('private static async Task<bool> PrintDirectAsync')]
        self.assertIn('Interlocked.CompareExchange', method)
        self.assertIn('finally', method)
        self.assertIn('return await', method)

    def test_unsupported_platform_fails_without_success_status(self):
        unsupported = (ROOT / 'POS.Client/Services/UnsupportedReceiptPrinter.cs').read_text(encoding='utf-8-sig')
        self.assertIn('Task<bool> PrintZReadingAsync', unsupported)
        self.assertIn('Task.FromException<bool>', unsupported)

if __name__ == '__main__':
    unittest.main()
