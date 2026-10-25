/**
 * 用 CDP 按精确视口截图。
 *
 * 为什么不用 chrome --screenshot --window-size=N:
 * Windows 版 Chrome 的窗口有最小宽度(约 500px)，--window-size=390 会被夹紧，
 * 页面实际按更大的宽度排版，再裁到 390 —— 看起来像"文字被切掉"，其实是截图方式错了。
 * CDP 的 Emulation.setDeviceMetricsOverride 才能真正把布局视口设成 390。
 *
 * 用法:
 *   node shoot.mjs <url> <width> <height> <out.png> [--mobile] [--scroll Y]
 */
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';

const [url, w, h, out] = process.argv.slice(2);
if (!url || !w || !h || !out) {
    console.error('用法: node shoot.mjs <url> <width> <height> <out.png> [--mobile] [--scroll Y]');
    process.exit(1);
}

const mobile = process.argv.includes('--mobile');
const scrollIdx = process.argv.indexOf('--scroll');
const scrollY = scrollIdx > 0 ? Number(process.argv[scrollIdx + 1]) : 0;

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = 9334 + Math.floor(Math.random() * 100);

const chrome = spawn(CHROME, [
    '--headless=new', '--disable-gpu', '--no-first-run', '--hide-scrollbars',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${process.env.TEMP}\\chrome-shoot-${Date.now()}`,
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
        } catch { /* 等待启动 */ }
        await sleep(250);
    }
    throw new Error('Chrome 调试端口没起来');
}

const ws = new WebSocket(await getWsUrl());
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let id = 0;
const pending = new Map();
ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
};
const send = (method, params = {}) => new Promise((resolve) => {
    const myId = ++id;
    pending.set(myId, resolve);
    ws.send(JSON.stringify({ id: myId, method, params }));
});

await send('Emulation.setDeviceMetricsOverride', {
    width: Number(w), height: Number(h), deviceScaleFactor: 1, mobile,
});
await send('Page.enable');
await send('Page.navigate', { url });
await sleep(2200);   // 等字体与入场动画

if (scrollY > 0) {
    await send('Runtime.evaluate', { expression: `window.scrollTo(0, ${scrollY})` });
    await sleep(600);
}

const shot = await send('Page.captureScreenshot', { format: 'png' });
if (!shot.result?.data) {
    console.error('截图失败', JSON.stringify(shot).slice(0, 300));
    process.exit(1);
}
writeFileSync(out, Buffer.from(shot.result.data, 'base64'));

const info = await send('Runtime.evaluate', {
    expression: `JSON.stringify({
        docWidth: document.documentElement.scrollWidth,
        viewWidth: document.documentElement.clientWidth,
        overflowing: document.documentElement.scrollWidth > document.documentElement.clientWidth
    })`,
    returnByValue: true,
});
console.log(`${out}  ${w}x${h}  ${info.result?.result?.value ?? ''}`);

ws.close();
chrome.kill();
process.exit(0);
