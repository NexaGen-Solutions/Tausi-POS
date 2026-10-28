// Captures the main screens in the current colours, for a person to look at (task B1). Nothing is
// compared pixel by pixel. Run against a Tausi Server and the web app (dev server or the server's own):
//
//   node capture.mjs fresh   a server that isn't set up: setup wizard step 1, pairing, splash
//   node capture.mjs demo    after `seed --demo`: PIN screen, table map, order, settle, admin pages, About
//
// Settings (environment): WEB_URL (default http://localhost:5173), SETUP_TOKEN_FILE (fresh),
// SEED_OUTPUT (demo: the seed's printed PIN table), TAUSI_ADMIN_PASSWORD (demo), OUT (default ./out).
import { mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium } from 'playwright';

const web = (process.env.WEB_URL ?? 'http://localhost:5173').replace(/\/$/, '');
const out = process.env.OUT ?? 'out';
mkdirSync(out, { recursive: true });

const browser = await chromium.launch();
// The terminal screens are designed for 1280×800 touch.
const newPage = async () => (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage();
const shot = async (page, name) => {
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: join(out, `${name}.png`) });
  console.log(`captured ${name}`);
};

async function fresh() {
  // Setup wizard, step 1 (behind the setup token).
  const wizard = await newPage();
  await wizard.goto(`${web}/setup`);
  const token = readFileSync(process.env.SETUP_TOKEN_FILE, 'utf8').trim();
  await wizard.getByLabel('Setup token').fill(token);
  await wizard.getByRole('button', { name: 'Start setup' }).click();
  await wizard.getByRole('heading', { name: 'Your business', exact: true }).waitFor();
  await shot(wizard, '06-setup-wizard-step-1');

  // Pairing: a browser that isn't paired.
  const pairing = await newPage();
  await pairing.goto(`${web}/connect`);
  await pairing.getByRole('heading', { name: 'Pairing code', exact: true }).waitFor();
  await pairing.locator('.setup__code').filter({ hasText: /\d{3} \d{3}/ }).waitFor();
  await shot(pairing, '07-pairing');

  // Splash: a paired terminal while the server doesn't answer yet.
  const splash = await newPage();
  await splash.route('**/api/health', () => {}); // never answers: stays on the splash
  await splash.addInitScript((server) => {
    localStorage.setItem('tausi.device', JSON.stringify({ serverUrl: server, deviceToken: 'screenshot' }));
  }, web);
  await splash.goto(`${web}/splash`);
  await splash.getByText(/^Connecting to /).waitFor();
  await splash.screenshot({ path: join(out, '08-splash.png') });
  console.log('captured 08-splash');

}

/** The seed's PIN table: "  edna            Waiter       1234". */
function pins() {
  const text = readFileSync(process.env.SEED_OUTPUT, 'utf8');
  return Object.fromEntries([...text.matchAll(/^\s+(\S+)\s+\S+\s+(\d{4,6})\s*$/gm)].map((m) => [m[1], m[2]]));
}

async function typePin(page, pin) {
  await page.getByRole('heading', { name: 'Enter your PIN', exact: true }).waitFor();
  for (const digit of pin) await page.getByRole('button', { name: digit, exact: true }).click();
  // The pad submits by itself at the PIN length; if it hasn't moved on, press Enter as a person would.
  await page.waitForURL((url) => !url.pathname.endsWith('/pos'), { timeout: 2000 }).catch(async () => {
    await page.getByRole('button', { name: 'Enter', exact: true }).click();
  });
}

/** Table 7's ticket, from the table map (one tap opens a table with one ticket). */
async function openTable7(page) {
  await page.getByRole('button', { name: /^7: / }).click();
  if (!/\/ticket\//.test(page.url())) await page.getByRole('button', { name: 'Open ticket' }).click();
  await page.waitForURL('**/pos/ticket/*');
  await page.getByRole('heading', { name: /Ticket #\d+/ }).waitFor();
}

async function demo() {
  const pin = pins();
  const admin = await newPage();
  await admin.goto(`${web}/admin/login`);
  await admin.getByText('Demo Bar').waitFor();
  await shot(admin, '10-admin-sign-in');
  await admin.getByLabel('User name').fill('admin');
  await admin.getByLabel('Password').fill(process.env.TAUSI_ADMIN_PASSWORD);
  await admin.getByRole('button', { name: 'Sign in' }).click();
  await admin.getByRole('link', { name: 'Terminals and printers' }).click();
  await admin.getByRole('heading', { name: 'Terminals and printers', exact: true }).waitFor();
  await shot(admin, '09-admin-terminals');

  // Pair a terminal as Counter A, through the admin's own session.
  const pos = await newPage();
  await pos.goto(`${web}/connect`);
  const code = (await pos.locator('.setup__code').filter({ hasText: /\d{3} \d{3}/ }).textContent()).replace(/\D/g, '');
  const terminals = await (await admin.request.get(`${web}/api/admin/terminals`)).json();
  const counterA = terminals.find((t) => t.name === 'Counter A');
  const paired = await admin.request.post(`${web}/api/admin/terminals/pair`, {
    data: { code, terminalId: counterA.id, confirmReplace: true },
    headers: { 'Idempotency-Key': crypto.randomUUID() },
  });
  if (!paired.ok()) throw new Error(`pairing failed: ${paired.status()} ${await paired.text()}`);
  await pos.waitForURL('**/pos');
  await pos.getByRole('heading', { name: 'Enter your PIN', exact: true }).waitFor();
  await shot(pos, '01-pin');

  await typePin(pos, pin.edna);
  await pos.waitForURL('**/pos/tables');
  await pos.getByRole('button', { name: /^7: / }).waitFor();
  await shot(pos, '02-table-map');

  await openTable7(pos);
  await shot(pos, '03-order');

  // Settling is the cashier's: log out and back in as CAROL.
  await pos.getByRole('button', { name: 'Tables' }).click();
  await pos.getByRole('button', { name: 'Log out' }).click();
  await typePin(pos, pin.CAROL);
  await pos.waitForURL('**/pos/tables');
  // In the app, not a page load: the PIN session lives in the page.
  await openTable7(pos);
  await pos.getByRole('button', { name: 'Settle', exact: true }).click();
  await pos.getByRole('heading', { name: /^Settle/ }).waitFor();
  await shot(pos, '04-settle');

  // About, as an unpaired terminal sees it (no session, so no licence details).
  const about = await newPage();
  await about.goto(`${web}/about`);
  await about.getByRole('heading', { name: 'About Tausi POS', exact: true }).waitFor();
  await shot(about, '11-about-terminal');

  await admin.goto(`${web}/admin/about`);
  await admin.getByRole('heading', { name: 'About Tausi POS', exact: true }).waitFor();
  await shot(admin, '12-about-admin');
}

/** Pairs a new browser as Counter A, through the admin's session; returns the PIN pad page. */
async function pairedTerminal(admin) {
  const pos = await newPage();
  await pos.goto(`${web}/connect`);
  const code = (await pos.locator('.setup__code').filter({ hasText: /\d{3} \d{3}/ }).textContent()).replace(/\D/g, '');
  const terminals = await (await admin.request.get(`${web}/api/admin/terminals`)).json();
  const counterA = terminals.find((t) => t.name === 'Counter A');
  const paired = await admin.request.post(`${web}/api/admin/terminals/pair`, {
    data: { code, terminalId: counterA.id, confirmReplace: true },
    headers: { 'Idempotency-Key': crypto.randomUUID() },
  });
  if (!paired.ok()) throw new Error(`pairing failed: ${paired.status()} ${await paired.text()}`);
  await pos.waitForURL('**/pos');
  return pos;
}

async function logOut(page) {
  await page.getByRole('button', { name: 'Log out' }).click();
  await page.getByRole('heading', { name: 'Enter your PIN', exact: true }).waitFor();
}

/** The pictures in docs/guides (after `seed --demo`): one set per guide, named for the guide. */
async function guides() {
  const pin = pins();
  const admin = await newPage();
  await admin.goto(`${web}/admin/login`);
  await admin.getByLabel('User name').fill('admin');
  await admin.getByLabel('Password').fill(process.env.TAUSI_ADMIN_PASSWORD);
  await admin.getByRole('button', { name: 'Sign in' }).click();
  await admin.getByRole('link', { name: 'Menu and prices' }).waitFor();

  // ---- Waiter ----
  const pos = await pairedTerminal(admin);
  await shot(pos, 'waiter-1-pin');
  await typePin(pos, pin.edna);
  await pos.waitForURL('**/pos/tables');
  await pos.getByRole('button', { name: /^7: / }).waitFor();
  await shot(pos, 'waiter-2-tables');

  // A free table: the first tap selects it, the second opens a new ticket.
  await pos.getByRole('button', { name: /^2: / }).click();
  if (!/\/ticket\//.test(pos.url())) await pos.getByRole('button', { name: /^2: / }).click();
  await pos.waitForURL(/\/pos\/ticket\//);
  await pos.getByRole('button', { name: /^Tusker Lager/ }).first().click();
  await pos.getByRole('dialog').waitFor();
  await shot(pos, 'waiter-3-cold-warm');
  await pos.getByRole('dialog').getByRole('button', { name: /^Add 1/ }).click();
  await pos.getByRole('dialog').waitFor({ state: 'detached' });
  await pos.getByRole('region', { name: 'Ticket' }).getByRole('button', { name: /Tusker Lager/ }).waitFor();
  await shot(pos, 'waiter-4-order');
  await pos.getByRole('button', { name: /^Print/ }).first().click();
  await pos.getByText(/Printed/).first().waitFor();
  await shot(pos, 'waiter-5-printed');

  await pos.getByRole('region', { name: 'Ticket' }).getByRole('button', { name: /Tusker Lager/ }).first().click();
  await pos.getByRole('button', { name: 'Void', exact: true }).click();
  await pos.getByRole('dialog').waitFor();
  await shot(pos, 'waiter-6-void');
  await pos.keyboard.press('Escape');
  await pos.getByRole('dialog').waitFor({ state: 'detached' });
  await pos.getByRole('button', { name: 'Tables', exact: true }).click();
  await pos.waitForURL('**/pos/tables');
  await pos.getByRole('button', { name: 'My sales', exact: true }).click();
  await pos.getByRole('heading', { name: /^My sales/ }).waitFor();
  await shot(pos, 'waiter-7-my-sales');
  await pos.getByRole('button', { name: 'Tables', exact: true }).click();
  await logOut(pos);

  // ---- Cashier ----
  await typePin(pos, pin.CAROL);
  await pos.waitForURL('**/pos/tables');
  await pos.getByRole('button', { name: 'My shift', exact: true }).click();
  await pos.waitForURL('**/pos/shift');
  await pos.getByRole('button', { name: 'Start shift', exact: true }).waitFor();
  await shot(pos, 'cashier-1-start-shift');
  await pos.getByLabel(/float/i).first().fill('5000').catch(() => {});
  await pos.getByRole('button', { name: 'Start shift', exact: true }).click();
  await pos.getByRole('button', { name: 'Start shift', exact: true }).waitFor({ state: 'detached' });
  await shot(pos, 'cashier-4-shift');
  await pos.getByRole('button', { name: 'Tables', exact: true }).click();
  await pos.waitForURL('**/pos/tables');
  await openTable7(pos);
  await pos.getByRole('button', { name: 'Settle', exact: true }).click();
  await pos.getByRole('heading', { name: /^Settle/ }).waitFor();
  await shot(pos, 'cashier-2-settle');
  await pos.getByRole('button', { name: 'M-Pesa', exact: true }).click();
  await shot(pos, 'cashier-3-mpesa');

  // ---- Supervisor ----
  await pos.goto(`${web}/pos`);
  await pos.getByRole('heading', { name: 'Enter your PIN', exact: true }).waitFor();
  await typePin(pos, pin.SAM);
  await pos.waitForURL('**/pos/tables');
  await pos.getByRole('button', { name: /^Approvals/ }).click();
  await pos.waitForURL('**/pos/approvals');
  await pos.getByRole('heading', { level: 1 }).first().waitFor();
  await shot(pos, 'supervisor-1-approvals');
  await pos.getByRole('button', { name: 'Tables', exact: true }).click();
  await pos.waitForURL('**/pos/tables');
  await pos.getByRole('button', { name: 'Close day', exact: true }).click();
  await pos.waitForURL('**/pos/day');
  await pos.getByRole('heading', { name: 'Close the day', exact: true }).waitFor();
  await shot(pos, 'supervisor-2-close-day');

  // ---- Owner / admin ----
  for (const [path, name] of [
    ['menu', 'owner-1-menu'], ['permissions', 'owner-3-permissions'], ['terminals', 'owner-4-terminals'],
    ['stock-counts', 'owner-6-counts'], ['reports', 'owner-8-reports'], ['settings/backups', 'owner-9-backups'],
    ['settings/system', 'owner-11-system'], ['settings/training', 'owner-12-training'],
  ]) {
    await admin.goto(`${web}/admin/${path}`);
    await admin.getByRole('heading', { level: 1 }).first().waitFor();
    await shot(admin, name);
  }
}

try {
  if (process.argv[2] === 'fresh') await fresh();
  else if (process.argv[2] === 'demo') await demo();
  else if (process.argv[2] === 'guides') await guides();
  else throw new Error('Usage: node capture.mjs fresh|demo|guides');
} catch (error) {
  // What every page showed when it went wrong, for whoever reads the CI artifact.
  let n = 0;
  for (const context of browser.contexts())
    for (const page of context.pages()) await page.screenshot({ path: join(out, `failed-${++n}.png`) }).catch(() => {});
  throw error;
} finally {
  await browser.close();
}
