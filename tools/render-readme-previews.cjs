// Render the built-in scenes as circular GIFs for the README.
// Requires Node.js plus playwright and sharp (no browser download needed on Windows).
// Install tools outside source: npm install --prefix artifacts/readme-preview-tools playwright sharp
// PowerShell: $env:NODE_PATH = "$PWD/artifacts/readme-preview-tools/node_modules"; node tools/render-readme-previews.cjs
const fs = require('node:fs/promises');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');
const sharp = require('sharp');

const root = path.resolve(__dirname, '..');
const size = 240;
const delay = 80;
const count = 100;

async function main() {
  const candidates = [
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe'];
  let executablePath;
  for (const candidate of candidates) {
    try { await fs.access(candidate); executablePath = candidate; break; } catch {}
  }
  if (!executablePath) throw new Error('Chrome or Edge was not found.');
  const env = { ...process.env };
  const browser = await chromium.launch({ executablePath, headless: true, env });
  try {
    const context = await browser.newContext({ viewport: { width: 480, height: 480 },
      deviceScaleFactor: 1, timezoneId: 'Asia/Shanghai' });
    await context.addInitScript(() => {
      const RealDate = Date;
      window.__captureTime = 0;
      window.Date = class extends RealDate {
        constructor(...args) { super(...(args.length ? args : [RealDate.UTC(2026, 10, 18, 10, 19) + window.__captureTime])); }
        static now() { return RealDate.UTC(2026, 10, 18, 10, 19) + window.__captureTime; }
      };
      window.requestAnimationFrame = callback => { window.__nextFrame = callback; return 1; };
    });
    for (const scene of ['clock', 'stars', 'fireworks']) {
      const page = await context.newPage();
      await page.goto(pathToFileURL(path.join(root, 'src/TURZX.WinUsbDisplay.Studio/Scenes', scene + '.html')).href);
      await page.addStyleTag({ content: 'html { background: transparent; } body { background: #000; border-radius: 50%; overflow: hidden; clip-path: circle(50%); }' });
      await page.evaluate(() => document.fonts.ready);
      const frames = [];
      for (let index = 0; index < count; index++) {
        await page.evaluate(time => { window.__captureTime = time; window.__nextFrame(time); }, index * delay);
        const png = await page.screenshot({ omitBackground: true, animations: 'allow' });
        const mask = Buffer.from('<svg width="240" height="240"><circle cx="120" cy="120" r="120" fill="white"/></svg>');
        frames.push(await sharp(png).resize(size, size).composite([{ input: mask, blend: 'dest-in' }]).png().toBuffer());
      }
      const output = path.join(root, 'docs/images', 'round-240-' + scene + '.gif');
      const encoded = await sharp(frames, { join: { animated: true } }).gif({ loop: 0, delay: Array(count).fill(delay), colours: 128, dither: 0.5 }).toBuffer();
      await fs.writeFile(output, encoded);
      const meta = await sharp(encoded, { animated: true }).metadata();
      if (meta.pages !== count || meta.loop !== 0 || meta.width !== size || meta.pageHeight !== size
          || !meta.delay.every(value => value === delay)) throw new Error('Invalid GIF metadata: ' + scene);
      const raw = await sharp(encoded, { animated: true }).ensureAlpha().raw().toBuffer();
      const frameBytes = size * size * 4;
      if (raw.subarray(0, frameBytes).equals(raw.subarray(frameBytes * 50, frameBytes * 51)))
        throw new Error('Animation frames did not change: ' + scene);
      if (raw[3] !== 0) throw new Error('Circular preview has an opaque corner: ' + scene);
      console.log(JSON.stringify({ scene, frames: meta.pages, width: meta.width, height: meta.pageHeight,
        durationMs: meta.delay.reduce((a,b) => a+b, 0), loop: meta.loop, bytes: (await fs.stat(output)).size }));
      await page.close();
    }
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
