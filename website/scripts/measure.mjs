/**
 * 用 Chrome DevTools Protocol 测量页面布局。
 * 为什么需要:无头截图看不出「是否横向溢出」,靠肉眼看边缘很容易判断错。
 * 这里直接读 scrollWidth / clientWidth,给出确定答案。
 *
 * 用法: node measure.mjs <url> <viewportWidth> <viewportHeight>
 * 依赖: Node 22+(内置 fetch 与 WebSocket)
 */
import { spawn } from 'node:child_process';

const url = process.argv[2] ?? 'http://127.0.0.1:8899/';
const vw = Number(process.argv[3] ?? 390);
const vh = Number(process.argv[4] ?? 844);

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = 9333;

const chrome = spawn(CHROME, [
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--hide-scrollbars',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${process.env.TEMP}\\chrome-cdp-${Date.now()}`,
    `--window-size=${vw},${vh}`,
    'about:blank',
], { stdio: 'ignore' });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function getWsUrl() {
    for (let i = 0; i < 40; i++) {
        try {
            const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
            const tabs = await res.json();
            const page = tabs.find((t) => t.type === 'page');
            if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
        } catch { /* 还没起来 */ }
        await sleep(250);
    }
    throw new Error('Chrome 调试端口没起来');
}

const wsUrl = await getWsUrl();
const ws = new WebSocket(wsUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let id = 0;
const pending = new Map();
ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
        pending.get(msg.id)(msg);
        pending.delete(msg.id);
    }
};

function send(method, params = {}) {
    const myId = ++id;
    return new Promise((resolve) => {
        pending.set(myId, resolve);
        ws.send(JSON.stringify({ id: myId, method, params }));
    });
}

// 设置视口
await send('Emulation.setDeviceMetricsOverride', {
    width: vw, height: vh, deviceScaleFactor: 1, mobile: vw < 600,
});

// 导航
await send('Page.enable');
await send('Page.navigate', { url });
await sleep(1800);

const expr = `(() => {
    const de = document.documentElement;
    const out = {
        viewport: ${vw} + 'x' + ${vh},
        docScrollWidth: de.scrollWidth,
        docClientWidth: de.clientWidth,
        overflowing: de.scrollWidth > de.clientWidth,
        offenders: []
    };
    // 找出所有超出视口右边界的元素
    const limit = de.clientWidth + 1;
    document.querySelectorAll('body *').forEach(el => {
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.right > limit) {
            out.offenders.push({
                tag: el.tagName.toLowerCase(),
                cls: (el.className || '').toString().slice(0, 60),
                right: Math.round(r.right),
                width: Math.round(r.width)
            });
        }
    });
    out.offenders = out.offenders.slice(0, 12);
    return JSON.stringify(out);
})()`;

const result = await send('Runtime.evaluate', { expression: expr, returnByValue: true });
console.log(result.result?.result?.value ?? JSON.stringify(result));

ws.close();
chrome.kill();
process.exit(0);
