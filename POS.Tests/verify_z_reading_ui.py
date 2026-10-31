"""UI wiring assertions only: no running app, database, or printer."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class ZReadingUiTests(unittest.TestCase):
    def test_generation_uses_real_contract_and_local_write_gate(self):
        page = (ROOT / 'POS.Client/Components/Pages/ZReading.razor').read_text(encoding='utf-8-sig')
        for flag in ['AllLocalOutboxesDrained = true', 'NoUnresolvedLocalTransactions = true', 'SoleDeviceCompletenessConfirmed = true', 'DeviceIdentifier =', 'WriteLock.WaitAsync()', 'WriteLock.Release()', 'GetBlockersAsync', 'Preferences.Default.Set(OperationKey']:
            self.assertIn(flag, page)
        self.assertNotIn('LocalSyncConfirmed', page)
    def test_saved_print_has_audit_and_reprint_identity(self):
        page = (ROOT / 'POS.Client/Components/Pages/ZReading.razor').read_text(encoding='utf-8-sig')
        for part in ['LoadPrintAuditAsync', 'ClientOperationId =', 'attempt.Report', 'attempt.IsReprint', 'Status = outcome', 'ErrorMessage = detail', 'pendingPrintAcknowledged', 'Print audit']:
            self.assertIn(part, page)
        printer = (ROOT / 'POS.Client/Platforms/Windows/WindowsReceiptPrinter.cs').read_text(encoding='utf-8-sig')
        self.assertIn('ZReadingHtmlFormatter.Format(report, isReprint)', printer)
    def test_cashier_access_and_duplicate_clicks_blocked(self):
        page = (ROOT / 'POS.Client/Components/Pages/ZReading.razor').read_text(encoding='utf-8-sig')
        self.assertIn('ShiftSession.IsSupervisorRole(Session.Role)', page)
        for action in ['GenerateAsync()', 'PrintAsync()']:
            body = page.split('private async Task ' + action)[1].split('try')[0]
            self.assertIn('if (busy', body)
            self.assertIn('busy = true', body)

if __name__ == '__main__': unittest.main()
