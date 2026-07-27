import { chromium } from 'playwright';
const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
await page.goto('http://127.0.0.1:5173/', { waitUntil: 'networkidle' });
await page.waitForSelector('.param-grid input');
await page.locator('.param-grid label', { hasText: 'Min EMA separation' }).locator('input').fill('0.008');
await page.getByRole('button', { name: 'Run backtest' }).click();
await page.waitForSelector('.chart-wrap canvas', { timeout: 300000 });

await page.getByRole('button', { name: 'Save…' }).click();
await page.waitForSelector('.modal');
await page.locator('.modal input').fill('BTC trend sep=0.008');
await page.locator('.modal textarea').fill('Chop filter at 0.008 cut fees from 16.9k to 8.5k. In-sample only.');
await page.screenshot({ path: '/tmp/save-modal.png' });
await page.locator('.modal button.primary').click();
await page.waitForSelector('.modal p:has-text("Saved")', { timeout: 20000 });
console.log('MODAL CONFIRMED SAVED');
await browser.close();
