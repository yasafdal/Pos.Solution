"""Offline regression checks for the shift UI; never starts MAUI/API or opens a DB.
Run: python scripts/check-shift-ui.py
Requires the .NET 10 SDK. The parser harness is created in Hermes scratch/TMPDIR.
"""
from pathlib import Path
import os
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
PAGES = ROOT / "POS.Client/Components/Pages"
sale = (PAGES / "NewSale.razor").read_text(encoding="utf-8-sig")
shift = (PAGES / "MyShift.razor").read_text(encoding="utf-8-sig")


def method(source, name):
    match = re.search(r"    private (?:static )?[^\n]+\b" + name + r"\([^\n]*\)\s*\{", source)
    assert match, f"Missing method: {name}"
    start = match.start()
    opening = source.index("{", match.start())
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


for name in ("ProceedToPayment", "ConfirmPayment"):
    body = method(sale, name)
    assert body.index("isProcessingPayment = true;") < body.index("await VerifyPaymentShift()"), f"{name}: acquire guard BEFORE verification yields"
    assert body.index("try") < body.index("await VerifyPaymentShift()") < body.index("catch (Exception"), f"{name}: verification exceptions must be caught"
    assert "finally" in body and "isProcessingPayment = false;" in body, f"{name}: all exits release guard"
    assert "Your cart is preserved" in body, f"{name}: actionable failure message"
    assert "await ProceedToPayment()" not in body, f"{name}: no recursive guarded entry"
print("PASS: payment guards acquired before shift await; exception/finally paths and no recursion")

opening = method(sale, "ConfirmOpeningFloat")
assert "OpenedAt = openingOpenedAt" in opening, "Opening ACK retries must reuse the exact timestamp"
assert opening.index("openingId = Guid.NewGuid()") > opening.index("await POS.Client.Services.ShiftSession.OpenAsync"), "Rotate opening identity only after acknowledgement"
assert opening.index("openingOpenedAt ??= DateTime.UtcNow") < opening.index("await POS.Client.Services.ShiftSession.OpenAsync"), "Capture opening time on first confirmation, not page load"
assert opening.index("openingOpenedAt = null") > opening.index("await POS.Client.Services.ShiftSession.OpenAsync"), "Reset timestamp with identity only after acknowledgement"
assert opening.index("finally { openingBusy = false; }") < opening.index("await ProceedToPayment()"), "Release opening guard before entering payment"
assert 'paymentShift.Status != "OPEN"' in opening, "A historical closed opening ACK cannot proceed to payment"
print("PASS: stable opening retry identity/timestamp and nonrecursive handoff after releasing guard")

for source, element, field, submit in ((sale, "opening-float", "openingFloatText", "ConfirmOpeningFloat"), (shift, "counted", "countedText", "Close")):
    match = re.search(r'<input\b[^>]*\bid="' + element + r'"[^>]*>', source)
    assert match, f"Missing input: {element}"
    tag = match.group(0)
    assert 'type="text"' in tag and 'inputmode="decimal"' in tag, f"{element}: preserve current displayed malformed text"
    assert f'@bind="{field}"' in tag and '@bind:event="oninput"' in tag, f"{element}: bind raw text on every input"
    assert re.search(r"private string " + field + r"\b", source), f"{element}: never retain a last-valid decimal binding"
    body = method(source, submit)
    assert f"TryParseCashAmount({field}, out var amount)" in body, f"{submit}: parse current text at submission"
    assert body.index(f"TryParseCashAmount({field}") < body.index("await "), f"{submit}: reject invalid input before I/O"
print("PASS: opening/closing capture current raw text and revalidate on submission")

parser = method(sale, "TryParseCashAmount")
assert parser == method(shift, "TryParseCashAmount"), "Both UI parsers must have the same rules"
program = '''using System;
using System.Globalization;
class Program
{
PARSER
    static void Main()
    {
        int checks = 0;
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            foreach (var text in new[] { "0", "0.00", "12.50", "9999999999999999.99", ".50", "100" })
            {
                if (!TryParseCashAmount(text, out var amount) || amount < 0) throw new Exception("Rejected valid: " + text);
                checks++;
            }
            foreach (var text in new string?[] { null, "", " ", "bad", "12oops", "12..5", "-0.01", "1,25", "1,000", "1e2", "NaN", "Infinity", "10000000000000000", "9999999999999999.991", "79228162514264337593543950336", "0.001", "1.00000000000000000000000000001" })
            {
                // Simulate a valid edit followed by an invalid/cleared edit: no cached valid value may survive.
                if (!TryParseCashAmount("50.00", out var prior)) throw new Exception("Setup failed");
                if (TryParseCashAmount(text, out var amount)) throw new Exception("Accepted invalid current text: " + text);
                checks++;
            }
        }
        Console.WriteLine($"PASS: {checks} executable parser cases across 3 cultures (including valid -> invalid edits)");
    }
}
'''.replace("PARSER", parser)
with tempfile.TemporaryDirectory(prefix="pos-shift-ui-", dir=os.environ.get("TMPDIR")) as tmp:
    folder = Path(tmp)
    (folder / "ParserCheck.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>', encoding="utf-8")
    (folder / "Program.cs").write_text(program, encoding="utf-8")
    subprocess.run(["dotnet", "run", "--project", str(folder / "ParserCheck.csproj"), "--verbosity", "quiet"], check=True)
