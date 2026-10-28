import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { setDeviceForTests } from '../../shared/device/device';
import { deviceInfo, fakeServer, json, renderRoutes } from '../../test/render';
import type { SettingEntry, Settings } from './types';

const handlers = vi.hoisted(() => new Map<string, ((payload: unknown) => void)[]>());
vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn((name: string, handler: (payload: unknown) => void) => {
    handlers.set(name, [...(handlers.get(name) ?? []), handler]);
    return () =>
      handlers.set(
        name,
        (handlers.get(name) ?? []).filter((h) => h !== handler),
      );
  }),
}));
const emit = (name: string, payload: unknown) =>
  act(() => (handlers.get(name) ?? []).forEach((h) => h(payload)));

function entry(
  key: string,
  type: SettingEntry['type'],
  value: unknown,
  extra: Partial<SettingEntry> = {},
): SettingEntry {
  return {
    key,
    type,
    label: key,
    help: `Help for ${key}`,
    value,
    default: value,
    min: null,
    max: null,
    allowed: null,
    reloadsTerminals: true,
    maxLength: 0,
    required: false,
    rule: null,
    editable: true,
    ...extra,
  };
}

function settingsWith(version = 7, logoFileId: string | null = null): Settings {
  return {
    version,
    logoFileId,
    logoPrintUrl: logoFileId ? `/api/admin/settings/logo/print.png?v=${logoFileId}` : null,
    sections: [
      {
        key: 'business',
        title: 'Business details',
        entries: [
          entry('business.name', 'String', "Demo Bar", {
            label: 'Business name',
            maxLength: 100,
          }),
          entry('business.logo', 'String', logoFileId, { label: 'Logo', editable: false }),
          entry('business.vatRate', 'Decimal', 16, {
            label: 'VAT rate (prices include VAT)',
            min: 0,
            max: 99.99,
          }),
          entry('business.timeZone', 'String', 'Africa/Nairobi', { label: 'Time zone' }),
          entry('business.receiptFooter', 'String', '', { label: 'Receipt footer' }),
        ],
      },
      {
        key: 'rules',
        title: 'Rules',
        entries: [
          entry('print.mode', 'Enum', 'OneButton', {
            label: 'One Print button',
            allowed: ['OneButton', 'SendAndBill'],
            rule: 1,
          }),
          entry('print.showVoidsOnReceipt', 'Bool', false, {
            label: 'Show voided items on the receipt',
            rule: 4,
          }),
          entry('pos.idleLogoutEnabled', 'Bool', true, {
            label: 'Log out after {minutes} minutes idle',
            rule: 8,
          }),
        ],
      },
      {
        key: 'pos',
        title: 'POS',
        entries: [
          entry('pos.idleLogoutMinutes', 'Int', 10, {
            label: 'Idle logout (minutes)',
            min: 1,
            max: 120,
          }),
        ],
      },
      {
        key: 'money',
        title: 'Money',
        entries: [
          entry('money.rounding', 'Enum', 'NearestOne', {
            label: 'Round totals',
            allowed: ['None', 'NearestOne', 'NearestFive', 'NearestTen'],
          }),
        ],
      },
    ],
  };
}

describe('Settings', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let settings: Settings;
  let extra: Parameters<typeof fakeServer>[0];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    handlers.clear();
    calls.length = 0;
    settings = settingsWith();
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
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'a', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () =>
          json(200, { name: "Demo Bar", logoFileId: null, currency: 'KES' }),
        'GET /api/admin/settings': () => json(200, settings),
        'GET /api/admin/settings/history': () => json(200, []),
        'POST /api/admin/settings/receipt-preview': ({ body }) => {
          const values = (body as { values: Record<string, unknown> }).values;
          return json(200, {
            width: (body as { width: number }).width,
            bill: `BILL for ${String(values['business.name'] ?? "Demo Bar")}`,
            receipt: 'RECEIPT No. 2041',
            logoPng: null,
          });
        },
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  const writes = (path: string) => calls.filter((c) => c.method !== 'GET' && c.path === path);

  it('shows business details and the rules; Save stays off until something changes and sends only the changed keys', async () => {
    const user = userEvent.setup();
    extra = {
      'PUT /api/admin/settings': ({ body }) => {
        const values = (body as { values: Record<string, unknown> }).values;
        settings = settingsWith(8);
        settings.sections[0].entries[0].value = values['business.name'];
        return json(200, settings);
      },
    };
    renderRoutes(routes, '/admin/settings');

    const save = await screen.findByRole('button', { name: 'Save changes' });
    expect(save).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'One Print button' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    expect(
      screen.getByRole('switch', { name: 'Log out after 10 minutes idle' }),
    ).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Idle logout (minutes)'));
    await user.type(screen.getByLabelText('Idle logout (minutes)'), '15');
    expect(
      screen.getByRole('switch', { name: 'Log out after 15 minutes idle' }),
    ).toBeInTheDocument();
    await user.clear(screen.getByLabelText('Idle logout (minutes)'));
    await user.type(screen.getByLabelText('Idle logout (minutes)'), '10');
    expect(save).toBeDisabled();

    await user.clear(screen.getByLabelText('Business name'));
    await user.type(screen.getByLabelText('Business name'), "Demo Bar Two");
    await user.click(screen.getByRole('switch', { name: 'One Print button' }));
    expect(save).toBeEnabled();
    await user.click(save);

    await waitFor(() => expect(writes('/api/admin/settings')).toHaveLength(1));
    expect(writes('/api/admin/settings')[0].body).toEqual({
      values: { 'business.name': "Demo Bar Two", 'print.mode': 'SendAndBill' },
      version: 7,
    });
    expect(await screen.findByText('Saved. Terminals updated.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  });

  it('shows field errors under each field', async () => {
    const user = userEvent.setup();
    extra = {
      'PUT /api/admin/settings': () =>
        json(400, {
          status: 400,
          detail: 'Some values need fixing.',
          code: 'invalid_fields',
          fieldErrors: { 'business.vatRate': 'Enter a number from 0 to 99.99.' },
        }),
    };
    renderRoutes(routes, '/admin/settings');
    const vat = await screen.findByLabelText('VAT rate (prices include VAT)');
    await user.clear(vat);
    await user.type(vat, '120');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Enter a number from 0 to 99.99.')).toBeInTheDocument();
    expect(vat).toHaveAttribute('aria-invalid', 'true');
    expect(vat).toHaveValue('120');
  });

  it('says someone else saved, and keeps what was typed', async () => {
    const user = userEvent.setup();
    extra = {
      'PUT /api/admin/settings': () =>
        json(409, {
          status: 409,
          detail: 'Someone else saved settings. Reload to see their changes.',
          code: 'settings_conflict',
        }),
    };
    renderRoutes(routes, '/admin/settings');
    const name = await screen.findByLabelText('Business name');
    await user.clear(name);
    await user.type(name, 'New name');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(
      await screen.findByText('Someone else saved settings. Reload to see their changes.'),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Reload' }));
    expect(screen.getByLabelText('Business name')).toHaveValue('New name');
  });

  it('asks before leaving with unsaved changes', async () => {
    const user = userEvent.setup();
    const router = renderRoutes(routes, '/admin/settings');
    const name = await screen.findByLabelText('Business name');
    await user.type(name, '!');

    await user.click(screen.getByRole('link', { name: 'Staff and PINs' }));
    const dialog = await screen.findByRole('dialog', { name: 'Leave without saving?' });
    await user.click(within(dialog).getByRole('button', { name: 'Stay and save' }));
    expect(router.state.location.pathname).toBe('/admin/settings');

    await user.click(screen.getByRole('link', { name: 'Staff and PINs' }));
    await user.click(await screen.findByRole('button', { name: 'Leave without saving' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/admin/staff'));
  });

  it('the receipt preview follows the form before saving', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/admin/settings');
    await user.click(await screen.findByRole('button', { name: 'Preview receipt' }));
    const panel = screen.getByRole('complementary', { name: 'Receipt preview' });
    expect(await within(panel).findByTestId('preview-bill')).toHaveTextContent(
      "BILL for Demo Bar",
    );

    await user.clear(screen.getByLabelText('Business name'));
    await user.type(screen.getByLabelText('Business name'), 'Unsaved Bar');

    await waitFor(
      () =>
        expect(within(panel).getByTestId('preview-bill')).toHaveTextContent('BILL for Unsaved Bar'),
      {
        timeout: 3000,
      },
    );
    await user.click(within(panel).getByRole('button', { name: '32 columns · 58 mm' }));
    await waitFor(() =>
      expect(writes('/api/admin/settings/receipt-preview').at(-1)!.body).toMatchObject({
        width: 32,
        values: { 'business.name': 'Unsaved Bar' },
      }),
    );
    expect(writes('/api/admin/settings')).toHaveLength(0);
  });

  it('uploads and removes the logo', async () => {
    // The file picker only offers PNG and JPG; a dropped GIF is still refused.
    const user = userEvent.setup({ applyAccept: false });
    extra = {
      'POST /api/admin/settings/logo': () => json(200, (settings = settingsWith(8, 'logo-web'))),
      'DELETE /api/admin/settings/logo': () => json(200, (settings = settingsWith(9))),
    };
    renderRoutes(routes, '/admin/settings');
    expect(await screen.findByText('No logo')).toBeInTheDocument();

    await user.upload(
      screen.getByLabelText('Upload logo'),
      new File(['x'], 'logo.gif', { type: 'image/gif' }),
    );
    expect(await screen.findByText('The logo must be a PNG or JPG picture.')).toBeInTheDocument();
    expect(writes('/api/admin/settings/logo')).toHaveLength(0);

    await user.upload(
      screen.getByLabelText('Upload logo'),
      new File(['png'], 'logo.png', { type: 'image/png' }),
    );
    expect(await screen.findByRole('img', { name: 'Logo as printed' })).toHaveAttribute(
      'src',
      '/api/admin/settings/logo/print.png?v=logo-web',
    );
    expect(writes('/api/admin/settings/logo')[0].body).toBeInstanceOf(FormData);

    await user.click(screen.getByRole('button', { name: 'Remove' }));
    expect(await screen.findByText('No logo')).toBeInTheDocument();
  });

  it('a terminal shows the new business name as soon as settings are saved', async () => {
    setDeviceForTests({ deviceToken: 'device-1' });
    let name = "Demo Bar";
    extra = {
      'GET /api/device': () => json(200, { ...deviceInfo(), businessName: name }),
      'GET /api/auth/pin/max-length': () => json(200, 4),
    };
    renderRoutes(routes, '/pos');
    expect(await screen.findByText("Demo Bar")).toBeInTheDocument();

    name = "Demo Bar Two";
    emit('settings.changed', { keys: ['business.name'] });

    expect(await screen.findByText("Demo Bar Two")).toBeInTheDocument();
    setDeviceForTests({ deviceToken: null });
  });
});
