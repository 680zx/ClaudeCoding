import { chromium } from 'playwright';

const out = process.argv[2] ?? '/tmp/shot.png';
const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });
const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });

const errors = [];
page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
page.on('pageerror', (e) => errors.push(String(e)));

await page.goto('http://127.0.0.1:5173/', { waitUntil: 'networkidle' });

// Fill in the chop filter that the earlier runs showed the strategy needs.
await page.waitForSelector('.param-grid input');
const sep = page.locator('.param-grid label', { hasText: 'Min EMA separation' }).locator('input');
await sep.fill('0.008');

await page.getByRole('button', { name: 'Run backtest' }).click();

// A real two-year hourly LEAN run takes tens of seconds.
await page.waitForSelector('.chart-wrap canvas', { timeout: 300000 });
await page.waitForTimeout(2500);

await page.screenshot({ path: out, fullPage: true });

const stats = await page.locator('.stat').allInnerTexts();
console.log('STATS:', JSON.stringify(stats));
console.log('CONSOLE ERRORS:', errors.length ? errors.slice(0, 5) : 'none');

await browser.close();
