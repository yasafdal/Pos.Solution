// Browser CSS/network fixture using actual Razor output; no running POS or database required.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const {spawn} = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const scratch = path.join(process.env.LOCALAPPDATA, 'hermes/cache/scratch');
const profile = fs.mkdtempSync(path.join(scratch, 'menu-browser-'));
const rendered = fs.readFileSync(path.join(scratch, 'menu-card-rendered.html'), 'utf8');
const css = ['wwwroot/lib/bootstrap/dist/css/bootstrap.min.css','wwwroot/app.css','Components/Pages/NewSale.razor.css','Components/Shared/ProductMenuCard.razor.css']
    .map(p => fs.readFileSync(path.join(root, 'POS.Client', p), 'utf8')).join('\n');
const cards = [...rendered.matchAll(/<button[\s\S]*?<\/button>/g)].map(m => m[0]);
let requests = 0;
const server = http.createServer((req, res) => {
    if (req.url.startsWith('/uploads/')) {
        requests++;
        res.writeHead(200, {'Content-Type':'image/svg+xml', 'Cache-Control':'public,max-age=3600'});
        return res.end('<svg xmlns="http://www.w3.org/2000/svg" width="300" height="100"><rect width="300" height="100" fill="green"/></svg>');
    }
    const selected = req.url === '/text' ? [cards[0]] : cards.slice(0, 3);
    res.setHeader('Content-Type', 'text/html');
    res.end('<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><style>'+css+'</style><div class="products-grid">'+selected.join('').replaceAll('https://pos.example', '')+'</div>');
});
(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', ['--headless=new','--no-first-run','--no-default-browser-check','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'], {stdio:'ignore'});
    let ws;
    try {
        const portFile = path.join(profile, 'DevToolsActivePort');
        let port;
        for (let i=0; i<100; i++) {
            try {
                const candidate = fs.readFileSync(portFile,'utf8').split('\n')[0];
                if (/^\d+$/.test(candidate) && Number(candidate)>0 && Number(candidate)<=65535) { port=candidate; break; }
            } catch (error) {
                if (!['ENOENT','EBUSY','EACCES'].includes(error.code)) throw error;
            }
            await new Promise(r=>setTimeout(r,100));
        }
        assert(port, 'Chrome did not publish a readable debug port within 10 seconds');
        const tabs = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
        ws = new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
        await new Promise(resolve=>ws.addEventListener('open', resolve, {once:true}));
        let id=0; const pending = new Map();
        ws.addEventListener('message', e=>{const m=JSON.parse(e.data); if(pending.has(m.id)){const {resolve,reject}=pending.get(m.id);pending.delete(m.id);m.error?reject(m.error):resolve(m.result);}});
        const send = (method,params={}) => new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});
        const evaluate = async expression => (await send('Runtime.evaluate',{expression,returnByValue:true})).result.value;
        await send('Page.enable');
        await send('Network.enable');
        const url = `http://127.0.0.1:${server.address().port}`;
        async function navigate(route) {
            await send('Page.navigate',{url:url+route});
            for(let i=0;i<50;i++){if(await evaluate(`location.pathname==='${route}' && document.readyState==='complete'`))break;await new Promise(r=>setTimeout(r,100));}
        }
        await navigate('/text');
        assert.equal(await evaluate('document.images.length'),0);
        assert.equal(requests,0,'Text Only requested a product image');
        await navigate('/images');
        for(const width of [1280,768,375]) {
            await send('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:false});
            await send('Input.dispatchKeyEvent',{type:'keyDown',key:'Tab',code:'Tab',windowsVirtualKeyCode:9});
            await send('Input.dispatchKeyEvent',{type:'keyUp',key:'Tab',code:'Tab',windowsVirtualKeyCode:9});
            await evaluate("document.querySelector('.product-card').focus()");
            const result = await evaluate(`(()=>{const cards=[...document.querySelectorAll('.product-card')];const image=document.querySelector('img');return {width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth,heights:cards.map(c=>c.getBoundingClientRect().height),colors:cards.map(c=>getComputedStyle(c).getPropertyValue('--category-color').trim()),fit:getComputedStyle(image).objectFit,placeholder:!!document.querySelector('.product-image-placeholder'),focus:getComputedStyle(cards[0]).outlineStyle,imageFrame:document.querySelector('.product-image-frame').getBoundingClientRect().height};})()`);
            assert.equal(result.overflow,false);
            assert(result.heights.every(h=>h>=44));
            assert(result.colors.every(c=>c==='#185132'));
            assert.equal(result.fit,'contain'); assert(result.placeholder); assert.equal(result.focus,'solid'); assert(result.imageFrame>0);
            console.log('PASS browser layout', JSON.stringify(result));
        }
        assert.equal(requests,1,'Image mode should fetch only the photo, not placeholders');
        console.log('PASS network: Text Only 0 photo requests; Image & Text 1 photo request');
    } finally {
        if(ws)ws.close(); chrome.kill(); server.close();
    }
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
