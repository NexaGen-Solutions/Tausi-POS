import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { fakeServer, json, renderRoutes } from '../test/render';
import type { LicenceStatus, SetupStatus, StepKey } from './types';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const keys: StepKey[] = ['business', 'admin', 'devices', 'menu', 'stock', 'licence'];

function status(
  done: Partial<Record<StepKey, 'Done' | 'Skipped'>> = {},
  extra: Partial<SetupStatus> = {},
): SetupStatus {
  const steps = keys.map((key) => ({ key, status: done[key] ?? ('Todo' as const), summary: null }));
  return {
    complete: false,
    unlocked: true,
    lockedUntil: null,
    steps,
    current: steps.find((s) => s.status === 'Todo')?.key ?? null,
    businessType: 'Bar',
    businessName: 'Demo Bar',
    ...extra,
  };
}

const trial: LicenceStatus = {
  status: 'Trial',
  isTrial: true,
  edition: 'Trial',
  business: 'Demo Bar',
  licenceId: null,
  terminalLimit: 2,
  terminalsUsed: 0,
  expiresAt: '2026-11-24T00:00:00Z',
  daysLeft: 30,
  supportUntil: null,
  overLimit: false,
  banner: 'Trial: 30 days left · Enter licence key',
};

describe('Setup wizard', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let current: SetupStatus;
  let extra: Parameters<typeof fakeServer>[0];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    current = status();
    extra = {};
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body,
      });
      return fakeServer({
        'GET /api/setup/status': () => json(200, current),
        'POST /api/setup/steps/devices/skip': () => {
          current = status({ business: 'Done', admin: 'Done', devices: 'Skipped' });
          return json(200, current);
        },
        'GET /api/setup/addresses': () =>
          json(200, [{ address: 'http://192.168.1.10:5080', interface: 'eth0' }]),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const posts = (path: string) => calls.filter((c) => c.method === 'POST' && c.path === path);

  it('asks for the setup token and says when it is locked', async () => {
    current = status({}, { unlocked: false });
    let tries = 0;
    extra['POST /api/setup/unlock'] = () =>
      ++tries < 2
        ? json(400, {
            status: 400,
            detail:
              "That isn't the setup token. It's printed in the server's console. 4 tries left.",
            code: 'setup_token_wrong',
          })
        : json(429, { status: 429, detail: 'Locked', lockedUntil: '2026-10-25T12:05:00Z' });
    renderRoutes(routes, '/setup');
    const field = await screen.findByLabelText('Setup token');
    expect(screen.getByRole('button', { name: 'Start setup' })).toBeDisabled();
    await userEvent.type(field, 'abcd-efgh');
    expect(field).toHaveValue('ABCD-EFGH');
    await userEvent.click(screen.getByRole('button', { name: 'Start setup' }));
    expect(await screen.findByText(/4 tries left/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Start setup' }));
    expect(
      await screen.findByText('Too many wrong tries. Setup is locked for 5 minutes.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/Locked until/)).toBeInTheDocument();
    expect(posts('/api/setup/unlock')[0].body).toEqual({ token: 'ABCD-EFGH' });
  });

  it('shows the six steps, resumes at the next unfinished one and marks done and skipped ones', async () => {
    current = status({ business: 'Done', admin: 'Done', devices: 'Skipped' });
    renderRoutes(routes, '/setup');
    expect(await screen.findByText('Step 4 of 6')).toBeInTheDocument();
    const list = screen.getByRole('list');
    const items = within(list).getAllByRole('button');
    expect(items.map((b) => b.textContent)).toEqual([
      '✓Your business (done)',
      '✓First admin and PIN (done)',
      '3Terminals and printers · Skipped (skipped)',
      '4Import the menu (to do)',
      '5Opening stock count (to do)',
      '6Licence key (to do)',
    ]);
    expect(items[3]).toHaveAttribute('aria-current', 'step');
    expect(screen.getByText('You can change all of this later in Settings.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Skip for now' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText('Step 3 of 6')).toBeInTheDocument();
  });

  it('step 1 saves the business and shows its errors; Next goes to the owner', async () => {
    let first = true;
    extra['PUT /api/setup/business'] = () => {
      if (first) {
        first = false;
        return json(400, {
          status: 400,
          fieldErrors: { 'business.timeZone': 'Choose a time zone from the list.' },
        });
      }
      current = status({ business: 'Done' });
      return json(200, current);
    };
    renderRoutes(routes, '/setup');
    expect(await screen.findByRole('heading', { name: 'Your business' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Skip for now' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Business name')).toHaveValue('Demo Bar');
    expect(screen.getByLabelText('VAT rate (prices include VAT)')).toHaveValue('16');
    await userEvent.click(screen.getByRole('button', { name: 'Next: first admin' }));
    expect(await screen.findByText('Choose a time zone from the list.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Next: first admin' }));
    expect(await screen.findByRole('heading', { name: 'First admin and PIN' })).toBeInTheDocument();
    expect(calls.filter((c) => c.method === 'PUT')[1].body).toMatchObject({
      name: 'Demo Bar',
      type: 'Bar',
      vatRate: 16,
      currency: 'KES',
    });
  });

  it('step 2 checks the owner, then shows the staff PINs once with the PIN sheet', async () => {
    current = status({ business: 'Done' });
    extra['POST /api/setup/admin'] = ({ body }) =>
      (body as { pin: string; pinAgain: string }).pin !== (body as { pinAgain: string }).pinAgain
        ? json(400, { status: 400, fieldErrors: { pinAgain: 'The two PINs are different.' } })
        : json(200, status({ business: 'Done', admin: 'Done' }));
    extra['POST /api/setup/staff'] = () =>
      json(200, {
        people: [{ id: 'u1', name: 'edna', role: 'Waiter', pin: '4729' }],
        pinSheetPdf: btoa('%PDF-1.4'),
      });
    renderRoutes(routes, '/setup');
    await userEvent.type(await screen.findByLabelText('Full name'), 'Wanjiku Njeri');
    await userEvent.type(screen.getByLabelText('Admin-site user name'), 'wanjiku');
    await userEvent.type(screen.getByLabelText('Admin-site password'), 'short');
    expect(screen.getByText('Too short: at least 10 characters.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Admin-site password'), ' and longer 9!');
    expect(screen.getByText('Strong.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('PIN (4 digits)'), '48a26');
    expect(screen.getByLabelText('PIN (4 digits)')).toHaveValue('4826');
    await userEvent.type(screen.getByLabelText('PIN again'), '4827');
    await userEvent.click(screen.getByRole('button', { name: 'Create the owner' }));
    expect(await screen.findByText('The two PINs are different.')).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText('PIN again'));
    await userEvent.type(screen.getByLabelText('PIN again'), '4826');
    await userEvent.click(screen.getByRole('button', { name: 'Create the owner' }));

    expect(await screen.findByText('The owner is set up and signed in.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Person 1'), 'edna');
    await userEvent.click(screen.getByRole('button', { name: '+ Add person' }));
    expect(screen.getByLabelText('Person 2')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Create staff and PINs' }));
    const pins = await screen.findByRole('region', { name: 'First PINs' });
    expect(pins).toHaveTextContent('These PINs are shown once.');
    expect(within(pins).getByText('4729')).toBeInTheDocument();
    expect(within(pins).getByRole('button', { name: 'Print PIN sheet' })).toBeInTheDocument();
    expect(posts('/api/setup/staff')[0].body).toEqual({
      people: [{ name: 'edna', role: 'waiter' }],
    });
  });

  it('step 3 shows the server address to type on each counter PC and can be skipped', async () => {
    current = status({ business: 'Done', admin: 'Done' });
    extra['GET /api/admin/terminals'] = () => json(200, []);
    extra['GET /api/admin/printers'] = () => json(200, []);
    extra['GET /api/licence'] = () => json(200, trial);
    renderRoutes(routes, '/setup');
    expect(await screen.findByText('http://192.168.1.10:5080')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Skip for now' }));
    expect(await screen.findByText('Step 4 of 6')).toBeInTheDocument();
    expect(posts('/api/setup/steps/devices/skip')).toHaveLength(1);
  });

  it('step 4 maps the file columns and previews with errors and counts', async () => {
    current = status({ business: 'Done', admin: 'Done', devices: 'Skipped' });
    extra['GET /api/admin/tables'] = () => json(200, { areas: [], tables: [] });
    extra['POST /api/admin/menu/import/preview'] = () =>
      json(200, {
        headers: ['Group', 'name', 'price'],
        mapping: ['ignore', 'name', 'price:l-std'],
        fields: [
          { field: 'ignore', label: 'Ignore' },
          { field: 'category', label: 'Category' },
          { field: 'name', label: 'Name' },
          { field: 'price:l-std', label: 'Price: Standard' },
        ],
        needsMapping: calls.filter((c) => c.path === '/api/admin/menu/import/preview').length === 1,
        mappingProblem: 'Choose the column with the category.',
        rows: [
          {
            line: 2,
            status: 'New',
            name: 'Mango Sunrise',
            category: 'Cocktails',
            changes: [],
            error: null,
          },
          {
            line: 3,
            status: 'Error',
            name: '',
            category: 'Cocktails',
            changes: [],
            error: 'The name is missing.',
          },
          {
            line: 4,
            status: 'Existing',
            name: 'Tusker Lager',
            category: 'Beers',
            changes: [],
            error: null,
          },
        ],
        newCategories: [],
        toDeactivate: [],
        new: 1,
        updated: 0,
        unchanged: 0,
        errors: 1,
        priceChanges: 1,
        existing: 1,
      });
    renderRoutes(routes, '/setup');
    expect(await screen.findByRole('link', { name: 'Download template' })).toHaveAttribute(
      'href',
      '/api/admin/menu/import/template',
    );
    await userEvent.click(screen.getByRole('button', { name: 'What each column means' }));
    expect(screen.getByText('The Standard price, VAT included.')).toBeInTheDocument();
    await userEvent.upload(
      screen.getByLabelText('Upload CSV or Excel'),
      new File(['Group,name,price'], 'menu.csv', { type: 'text/csv' }),
    );
    expect(await screen.findByText('Choose the column with the category.')).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText('Group'), 'category');
    expect(
      await screen.findByText(
        '1 items ready, 1 with errors, 1 duplicates of existing items (will be skipped)',
      ),
    ).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Preview' });
    expect(within(table).getByText('The name is missing.').closest('tr')).toHaveClass(
      'wizard__row--error',
    );
    expect(within(table).getByText('Already on the menu: skipped')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download the skipped rows' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Import 1 items' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Add tables' })).toBeInTheDocument();
  });

  it('step 4 tests the SambaPOS connection and previews what comes across', async () => {
    current = status({ business: 'Done', admin: 'Done', devices: 'Skipped' });
    extra['GET /api/admin/tables'] = () => json(200, { areas: [], tables: [{}] });
    let tested = 0;
    extra['POST /api/admin/menu/import/sambapos/test'] = () =>
      ++tested === 1
        ? json(400, { status: 400, detail: "Couldn't connect to SambaPOS: Login failed." })
        : json(200, {
            message: 'Connected to SambaPOS5 on POS-SERVER\\SQLEXPRESS: 212 menu items.',
          });
    extra['POST /api/admin/menu/import/sambapos/preview'] = () =>
      json(200, {
        items: [
          {
            name: 'Tusker',
            category: 'Beers',
            status: 'New',
            prices: ['Standard: 250', 'Happy Hour: 200'],
          },
        ],
        categories: ['Beers'],
        priceLists: ['Happy Hour', 'Standard'],
        modifierGroups: ['Temperature (Cold / Warm)'],
        tables: ['T1', 'T2'],
        users: [
          { id: '7', name: 'Zed', sambaRole: 'Cleaner', role: null, status: 'Choose a role' },
        ],
        newItems: 1,
        existingItems: 0,
        newTables: 2,
        newUsers: 0,
        problems: ['OrderTagMaps: no MenuItemId column, skipped.'],
      });
    renderRoutes(routes, '/setup');
    await userEvent.click(await screen.findByRole('button', { name: 'From SambaPOS' }));
    expect(screen.getByText(/Tausi only reads from SambaPOS/)).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('SQL Server'), 'POS-SERVER\\SQLEXPRESS');
    expect(screen.getByLabelText('Database')).toHaveValue('SambaPOS5');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    expect(
      await screen.findByText("Couldn't connect to SambaPOS: Login failed."),
    ).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    expect(await screen.findByText(/212 menu items/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Preview' }));
    expect(
      await screen.findByText(
        '1 new items (0 already here), 2 price lists, 1 modifier groups, 2 new tables, 0 new staff',
      ),
    ).toBeInTheDocument();
    expect(screen.getByText('OrderTagMaps: no MenuItemId column, skipped.')).toBeInTheDocument();
    const parts = screen.getByRole('group', { name: 'What to bring across' });
    await userEvent.click(within(parts).getByLabelText('Users'));
    expect(within(parts).getByLabelText('Users')).not.toBeChecked();
    expect(screen.getByLabelText('Zed (Cleaner)')).toHaveValue('');
    expect(posts('/api/admin/menu/import/sambapos/test')[0].body).toMatchObject({
      server: 'POS-SERVER\\SQLEXPRESS',
      windowsLogin: true,
    });
    expect(posts('/api/admin/menu/import/sambapos/preview')[0].body).toMatchObject({
      options: { firstPriceOnly: true },
    });

    // Changing it clears the preview: the items it shows depend on it.
    await userEvent.click(screen.getByLabelText(/One price per item/));
    expect(screen.queryByRole('group', { name: 'What to bring across' })).not.toBeInTheDocument();
  });

  it('step 6 says why a key is refused and starts the trial', async () => {
    current = status({
      business: 'Done',
      admin: 'Done',
      devices: 'Skipped',
      menu: 'Skipped',
      stock: 'Skipped',
    });
    extra['POST /api/setup/licence'] = () =>
      json(400, {
        status: 400,
        detail: "This key is for 'Other Bar'.",
        code: 'licence_wrong_business',
      });
    extra['POST /api/setup/licence/trial'] = () => json(200, trial);
    renderRoutes(routes, '/setup');
    expect(await screen.findByRole('button', { name: 'Next: finish' })).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Licence key'), 'TAUSI-AAAAA');
    await userEvent.click(screen.getByRole('button', { name: 'Use this key' }));
    expect(await screen.findByText("This key is for 'Other Bar'.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Start 30-day trial' }));
    expect(await screen.findByText('Trial started: 30 days, 2 terminals.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next: finish' })).toBeEnabled();
  });

  it('finish lists what was set up and what was skipped with where to do it', async () => {
    current = status({
      business: 'Done',
      admin: 'Done',
      devices: 'Skipped',
      menu: 'Done',
      stock: 'Skipped',
      licence: 'Done',
    });
    extra['GET /api/setup/summary'] = () =>
      json(200, {
        lines: [
          {
            step: 'business',
            title: 'Your business',
            text: 'Demo Bar',
            skipped: false,
            laterLink: null,
          },
          {
            step: 'devices',
            title: 'Terminals and printers',
            text: 'Skipped. 0 printers',
            skipped: true,
            laterLink: '/admin/terminals',
          },
        ],
      });
    extra['POST /api/setup/finish'] = () => new Response(null, { status: 204 });
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign, pathname: '/setup' });
    renderRoutes(routes, '/setup');
    expect(await screen.findByRole('heading', { name: 'Ready to trade' })).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'Do it now' })).toHaveAttribute(
      'href',
      '/admin/terminals',
    );
    await userEvent.click(screen.getByRole('button', { name: 'Finish setup' }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith('/admin'));
  });

  it('says setup is complete once it is', async () => {
    current = status({}, { complete: true, unlocked: false });
    renderRoutes(routes, '/setup');
    expect(
      await screen.findByRole('heading', { name: 'Setup is already complete' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Use as a terminal' })).toHaveAttribute(
      'href',
      '/connect',
    );
  });
});

describe('Licence', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let licence: LicenceStatus;

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    licence = trial;
    fetchMock.mockImplementation(async (input, init) =>
      fakeServer({
        'GET /api/auth/me': () =>
          json(200, {
            user: { id: 'a', name: 'Administrator', role: 'Admin' },
            permissions: { 'settings.manage': 'Allowed' },
          }),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', logoFileId: null, currency: 'KES' }),
        'GET /api/licence': () => json(200, licence),
        'POST /api/admin/licence': () =>
          json(400, {
            status: 400,
            detail: "That licence key isn't valid. Check it was copied completely.",
          }),
      })(input, init),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it('counts the trial down in a banner and shows the licence page', async () => {
    renderRoutes(routes, '/admin/settings/licence');
    expect(await screen.findByText('Trial: 30 days left · Enter licence key')).toBeInTheDocument();
    expect(await screen.findByText('0 of 2')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('New licence key'), 'TAUSI-BROKEN');
    await userEvent.click(screen.getAllByRole('button', { name: 'Enter licence key' }).at(-1)!);
    expect(
      await screen.findByText("That licence key isn't valid. Check it was copied completely."),
    ).toBeInTheDocument();
  });

  it('shows the stronger banner after the expiry', async () => {
    licence = {
      ...trial,
      isTrial: false,
      status: 'Expired',
      banner: 'Your licence has ended. Selling, printing and settling still work.',
    };
    renderRoutes(routes, '/admin/settings/licence');
    expect(await screen.findByRole('alert')).toHaveTextContent('Your licence has ended.');
  });
});
