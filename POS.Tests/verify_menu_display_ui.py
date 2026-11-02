"""Integration guards for terminal menu settings; rendered behavior lives in POS.Client.Tests."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class MenuDisplayUiTests(unittest.TestCase):
    def test_terminal_form_loads_and_preserves_mode_on_status_change(self):
        page = (ROOT / "POS.Web/Components/Pages/ManageTerminals.razor").read_text(encoding="utf-8")
        self.assertIn("POS Display Settings", page)
        self.assertIn('@bind-Value="currentTerminal.MenuDisplayMode"', page)
        self.assertIn('For="@(() => currentTerminal.MenuDisplayMode)"', page)
        self.assertIn('value="@MenuDisplayMode.TextOnly"', page)
        self.assertIn('value="@MenuDisplayMode.ImageAndText"', page)
        self.assertEqual(page.count("MenuDisplayMode = terminal.MenuDisplayMode"), 2,
                         "Edit and activate/deactivate must both preserve the mode")

    def test_both_catalog_views_use_one_card_and_existing_selection(self):
        page = (ROOT / "POS.Client/Components/Pages/NewSale.razor").read_text(encoding="utf-8")
        self.assertEqual(page.count("<ProductMenuCard "), 2)
        self.assertEqual(page.count('OnSelect="() => AddToCart(product)"'), 2)
        self.assertEqual(page.count('DisplayMode="@(Session.Terminal?.MenuDisplayMode ?? MenuDisplayMode.TextOnly)"'), 2)
        self.assertIn('CategoryColor="@catColor"', page)

    def test_photo_requests_exist_only_in_image_mode(self):
        path = ROOT / "POS.Client/Components/Shared/ProductMenuCard.razor"
        self.assertTrue(path.exists(), "Shared card is missing")
        card = path.read_text(encoding="utf-8")
        self.assertIn("DisplayMode == MenuDisplayMode.ImageAndText", card)
        self.assertIn('loading="lazy"', card)
        self.assertIn('decoding="async"', card)
        self.assertIn("@onerror", card)
        self.assertIn("imageFailed", card)
        self.assertIn("--category-color:", card)


if __name__ == "__main__":
    unittest.main()
