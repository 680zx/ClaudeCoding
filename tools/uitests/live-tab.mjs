// Exercises the Live tab's tile CRUD through the real UI, not the API directly
// (spec 7, phase 3): add an alpha, toggle it, edit it, then delete it.
import { chromium } from 'playwright';

const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' });
const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });

const errors = [];
page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
page.on('pageerror', (e) => errors.push(String(e)));

const status = async (label) => {
  const text = await page.locator('.panel p.muted').first().innerText();
  console.log(`${label.padEnd(22)} ${text.replace(/\s+/g, ' ')}`);
};

await page.goto('http://127.0.0.1:5173/', { waitUntil: 'networkidle' });
await page.getByRole('button', { name: 'Live' }).click();
await page.waitForSelector('.live-header');
await status('initial:');

// --- Add -------------------------------------------------------------------
await page.getByRole('button', { name: 'Add alpha' }).click();
await page.waitForSelector('.modal.wide');
await page.locator('.modal .control-row input[type="text"]').first().fill('BTC trend live');
await page.locator('.modal input[type="checkbox"]').check();
await page.locator('.modal .param-grid label', { hasText: 'Min EMA separation' }).locator('input').fill('0.008');
await page.locator('.modal button.primary').click();
await page.waitForSelector('.tile.on', { timeout: 20000 });
console.log('added         :', await page.locator('.tile h3').first().innerText(),
            '|', await page.locator('.tile .badge').first().innerText());
await status('after add:');
await page.screenshot({ path: '/tmp/live-tab.png', fullPage: true });

// --- Disable (soft) --------------------------------------------------------
await page.locator('.tile').first().getByRole('button', { name: 'Disable' }).click();
await page.waitForSelector('.tile .badge.off', { timeout: 20000 });
console.log('after disable :', await page.locator('.tile .badge').first().innerText());
await status('after disable:');

// --- Edit ------------------------------------------------------------------
await page.locator('.tile').first().getByRole('button', { name: 'Edit' }).click();
await page.waitForSelector('.modal.wide');
await page.locator('.modal .control-row input[type="text"]').first().fill('BTC trend live (edited)');
await page.locator('.modal .param-grid label', { hasText: 'Fast EMA period' }).locator('input').fill('15');
await page.locator('.modal button.primary').click();
await page.waitForFunction(
  () => document.querySelector('.tile h3')?.textContent?.includes('edited'), null, { timeout: 20000 });
console.log('after edit    :', await page.locator('.tile h3').first().innerText(),
            '|', (await page.locator('.tile .chip').allInnerTexts()).slice(0, 2).join(', '));

// --- Re-enable, then delete ------------------------------------------------
await page.locator('.tile').first().getByRole('button', { name: 'Enable' }).click();
await page.waitForSelector('.tile .badge.on', { timeout: 20000 });
await status('after re-enable:');

page.on('dialog', (d) => d.accept());
await page.locator('.tile').first().getByRole('button', { name: 'Delete' }).click();
await page.waitForFunction(() => document.querySelectorAll('.tile.on, .tile.off').length === 0, null, { timeout: 20000 });
console.log('after delete  : no tiles remain');
await status('after delete:');

console.log('CONSOLE ERRORS:', errors.length ? errors.slice(0, 5) : 'none');
await browser.close();
