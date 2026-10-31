"""Scratch-only Edge CSS fixture. Does not start POS or access its data/printers."""
from pathlib import Path
import html
import json
import os
import re
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path.home() / 'AppData/Local/hermes/cache/scratch'
run = SCRATCH / ('z-reading-layout-' + uuid.uuid4().hex[:10])
run.mkdir(parents=True)
edge = Path('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe')
assert edge.exists(), 'Edge is required for this local fixture.'
css = '\n'.join((ROOT / p).read_text(encoding='utf-8-sig') for p in [
    'POS.Client/wwwroot/lib/bootstrap/dist/css/bootstrap.min.css',
    'POS.Client/wwwroot/app.css',
    'POS.Client/Components/Pages/ZReading.razor.css'])
body = '''<section class="z-reading-page"><h3>Z-Reading — End of Day</h3>
<div class="z-reading-notice">Finalize on the original terminal only. Generation saves an immutable report; printing is separate.</div>
<div class="z-reading-controls"><div><label for="terminal">Terminal</label><input id="terminal" class="form-control" readonly value="Original POS terminal with long name"></div>
<div><label for="date">Business date</label><input id="date" type="date" class="form-control" value="2026-10-29"></div><button class="btn btn-outline-primary">Check readiness / Load saved</button></div>
<div class="z-reading-checks"><h5>Readiness checks</h5><ul><li>Unsynchronized records must be resolved before finalization.</li></ul></div>
<label class="z-reading-confirm"><input type="checkbox"><span>I have synchronized every device and checked all pending payments. No offline copy has unuploaded transactions.</span></label>
<div class="z-reading-actions"><button class="btn btn-primary">Generate &amp; Finalize Z-Reading</button></div>
<div class="z-reading-actions"><div class="print-reason"><label for="reason">Print / reprint reason</label><input id="reason" class="form-control" value="Paper jam recovery"></div><button class="btn btn-primary">Print saved report / Reprint</button></div>
<details class="z-reading-audit" open><summary>Print audit (2 attempts)</summary><ul><li>2026-10-30 — Manager — UNKNOWN — Original attempt — Printer dialog closed.</li></ul></details>
<iframe class="z-reading-preview" title="Saved report preview" srcdoc="&lt;html&gt;&lt;body&gt;Saved Z report preview&lt;/body&gt;&lt;/html&gt;"></iframe></section>'''
script = '''<script>addEventListener('load',()=>{setTimeout(()=>{
const r={width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,
controls:[...document.querySelectorAll('button,input:not([type=checkbox])')].map(x=>({tag:x.tagName,w:x.getBoundingClientRect().width,h:x.getBoundingClientRect().height})),
preview:document.querySelector('iframe').getBoundingClientRect().width,
background:getComputedStyle(document.querySelector('.z-reading-controls')).backgroundColor};
document.body.insertAdjacentHTML('beforeend','<pre id="metrics">'+JSON.stringify(r)+'</pre>');},150);});</script>'''
fixture = run / 'fixture.html'
fixture.write_text('<!doctype html><html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{padding:16px;}' + css + '</style><body>' + body + script + '</body></html>', encoding='utf-8')
results = []
for width, height in [(1280, 800), (500, 850), (850, 400)]:
    command = [str(edge), '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-features=msEdgeSidebarV2', '--allow-file-access-from-files', '--user-data-dir=' + str(run / f'profile-{width}'), f'--window-size={width},{height}', '--virtual-time-budget=1500', '--dump-dom', fixture.as_uri()]
    completed = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=45)
    (run / f'dom-{width}.html').write_text(completed.stdout, encoding='utf-8')
    match = re.search(r'<pre id="metrics">(\{.*?\})</pre>', completed.stdout)
    assert match, f'Edge did not return layout metrics: {completed.stderr[-1000:]}'
    result = json.loads(html.unescape(match.group(1)))
    assert result['scrollWidth'] <= result['width'], result
    assert result['preview'] <= result['width'], result
    assert all(x['h'] >= 44 and x['w'] > 0 for x in result['controls']), result
    results.append(result)
(run / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
print(json.dumps({'fixtures': len(results), 'results': results, 'artifact': str(run / 'results.json')}, indent=2))
