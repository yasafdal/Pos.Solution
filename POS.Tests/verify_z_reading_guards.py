"""Deployment safety checks; never executes SQL or connects to a database."""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]

class ZReadingGuardScriptTests(unittest.TestCase):
    def test_all_fiscal_sources_are_fenced(self):
        path = ROOT / 'POS.Api/Sql/z-reading-period-guards.sql'
        self.assertTrue(path.exists(), 'Manual finalization guard script is required.')
        sql = path.read_text(encoding='utf-8')
        for name in ['Orders', 'OrderItems', 'OrderItemModifiers', 'Payments', 'CashDrawerShifts', 'CashDrawerMovements', 'SalesReturns', 'SalesReturnItems', 'Refunds', 'TransactionVoids']:
            self.assertIn(name, sql)
        self.assertIn('FOR UPDATE', sql)
        self.assertIn('MutationVersion', sql)
        self.assertIn('RAISE EXCEPTION', sql)
        self.assertIn('DELETE', sql)
        self.assertIn('ROLLBACK;', sql)
        self.assertNotIn('DISABLE TRIGGER', sql)
        # TRUNCATE needs its own statement trigger: row guards never see it.
        self.assertIn('BEFORE TRUNCATE', sql)
        self.assertIn('pos_z_snapshot_fence', sql)
        self.assertIn('FOR SHARE', sql)
        for name in ['HeldSales', 'HeldSaleItems', 'HeldSaleItemModifiers', 'HeldSaleAuditLogs']:
            self.assertIn(name, sql)
        import re
        executable = re.sub(r'--[^\n]*', '', sql).strip()
        self.assertTrue(executable.startswith('ROLLBACK;'))
        self.assertTrue(executable.endswith('ROLLBACK;'))
        self.assertNotRegex(executable, r'\bCOMMIT\s*;')

if __name__ == '__main__': unittest.main()
