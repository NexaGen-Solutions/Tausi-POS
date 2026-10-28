import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { api } from '../../shared/api/client';
import { setServerMode } from '../../shared/connection/serverState';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type { BackupRecord } from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const me = {
  user: { id: 'a', name: 'Administrator', role: 'Admin' },
  permissions: { 'settings.manage': 'Allowed' },
};

const backup = (over: Partial<BackupRecord> = {}): BackupRecord => ({
  name: 'tausi-20261025-020000-nightly.tausibak',
  createdAt: '2026-10-25T02:00:00Z',
  reason: 'Nightly',
  size: 12 * 1024 * 1024,
  verified: true,
  encrypted: false,
  business: "Demo Bar",
  version: '1.0.0',
  copiedTo: 'E:\\Backups',
  copyError: null,
  ...over,
});

describe('Settings → Backups and System', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let extra: Parameters<typeof fakeServer>[0];
  let status: { state: string; lastSuccessAt: string | null; message: string | null };

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    extra = {};
    status = { state: 'ok', lastSuccessAt: '2026-10-25T02:00:00Z', message: null };
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname + url.search,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body,
      });
      return fakeServer({
        'GET /api/auth/me': () => json(200, me),
        'GET /api/brand': () =>
          json(200, { name: "Demo Bar", logoFileId: null, currency: 'KES' }),
        'GET /api/licence': () => json(200, { banner: null, status: 'Active' }),
        'GET /api/backups/status': () => json(200, status),
        'GET /api/admin/backups': () =>
          json(200, [
            backup(),
            backup({
              name: 'b2.tausibak',
              reason: 'Manual',
              encrypted: true,
              copyError: 'unplugged',
              copiedTo: null,
            }),
          ]),
        'GET /api/admin/backups/settings': () =>
          json(200, {
            time: '05:00',
            copyFolder: 'E:\\Backups',
            encrypt: false,
            hasPassword: false,
          }),
        'GET /health': () =>
          json(200, {
            status: 'ok',
            version: '1.0.0',
            server: 'POS-SERVER',
            database: 'ok',
            diskFreeBytes: 50 * 1024 ** 3,
            lastBackupAt: '2026-10-25T02:00:00Z',
            backup: 'ok',
            backupMessage: null,
            licence: 'Active',
            minimumTerminalVersion: '1.0.0',
          }),
        'GET /api/admin/system': () =>
          json(200, {
            version: '1.0.0',
            platform: 'windows-x64',
            minimumTerminalVersion: '1.0.0',
            cantInstallReason: null,
          }),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    setServerMode('normal');
  });

  it('lists backups with their check and copy, and backs up now', async () => {
    extra['POST /api/admin/backups'] = () =>
      json(200, backup({ name: 'new.tausibak', size: 2 * 1024 * 1024 }));
    renderRoutes(routes, '/admin/settings/backups');
    const table = await screen.findByRole('table', { name: 'Backups on this server' });
    await within(table).findAllByText('12.0 MB');
    const rows = within(table).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Nightly');
    expect(rows[1]).toHaveTextContent('12.0 MB');
    expect(rows[1]).toHaveTextContent('E:\\Backups');
    expect(rows[2]).toHaveTextContent('Encrypted');
    expect(rows[2]).toHaveTextContent('Copy failed');
    await userEvent.click(screen.getByRole('button', { name: 'Back up now' }));
    expect(await screen.findByText('Backed up and checked (2.0 MB).')).toBeInTheDocument();
  });

  it('warns before encrypting and asks for a password', async () => {
    extra['PUT /api/admin/backups/settings'] = () =>
      json(400, { status: 400, fieldErrors: { password: 'Choose a password for the backups.' } });
    renderRoutes(routes, '/admin/settings/backups');
    await userEvent.click(
      await screen.findByRole('switch', { name: 'Encrypt backups with a password' }),
    );
    expect(screen.getByRole('note')).toHaveTextContent("can't be restored, by anyone");
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Choose a password for the backups.')).toBeInTheDocument();
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({
      time: '05:00',
      copyFolder: 'E:\\Backups',
      encrypt: true,
      password: null,
    });
  });

  it('restores after showing what the backup holds, and asks for the name of another business', async () => {
    extra['POST /api/admin/backups/tausi-20261025-020000-nightly.tausibak/inspect'] = () =>
      json(200, {
        business: 'Old Bar',
        createdAt: '2026-10-25T02:00:00Z',
        version: '1.0.0',
        schema: 's',
        provider: 'SqlServer',
        encrypted: false,
      });
    let tries = 0;
    extra['POST /api/admin/backups/tausi-20261025-020000-nightly.tausibak/restore'] = () =>
      ++tries === 1
        ? json(400, {
            status: 400,
            code: 'backup_other_business',
            detail: "This backup is from 'Old Bar'.",
          })
        : new Response(null, { status: 204 });
    const assign = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign });
    renderRoutes(routes, '/admin/settings/backups');
    const table = await screen.findByRole('table', { name: 'Backups on this server' });
    await within(table).findAllByText('12.0 MB');
    await userEvent.click(
      within(within(table).getAllByRole('row')[1]).getByRole('button', { name: 'Restore' }),
    );
    const dialog = await screen.findByRole('dialog', { name: 'Restore a backup' });
    expect(await within(dialog).findByText('Old Bar')).toBeInTheDocument();
    expect(
      within(dialog).getByText(/A backup of how things are now is taken first/),
    ).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Restore' }));
    await userEvent.type(
      await within(dialog).findByLabelText("Type Old Bar to restore another business's backup"),
      'Old Bar',
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Restore' }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith('/admin/settings/backups'));
    expect(calls.filter((c) => c.path.endsWith('/restore')).at(-1)?.body).toEqual({
      password: null,
      confirmBusiness: 'Old Bar',
    });
  });

  it('asks for the password of an encrypted backup before opening it', async () => {
    extra['POST /api/admin/backups/b2.tausibak/inspect'] = ({ body }) =>
      (body as { password: string }).password === 'right'
        ? json(200, {
            business: "Demo Bar",
            createdAt: '2026-10-25T02:00:00Z',
            version: '1.0.0',
            schema: 's',
            provider: 'Postgres',
            encrypted: true,
          })
        : json(400, {
            status: 400,
            code: 'backup_password',
            detail: "That password doesn't open this backup.",
          });
    renderRoutes(routes, '/admin/settings/backups');
    const table = await screen.findByRole('table', { name: 'Backups on this server' });
    await within(table).findAllByText('12.0 MB');
    await userEvent.click(
      within(within(table).getAllByRole('row')[2]).getByRole('button', { name: 'Restore' }),
    );
    const dialog = await screen.findByRole('dialog');
    await userEvent.type(within(dialog).getByLabelText('Backup password'), 'wrong');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Open the backup' }));
    expect(
      await within(dialog).findByText("That password doesn't open this backup."),
    ).toBeInTheDocument();
    await userEvent.clear(within(dialog).getByLabelText('Backup password'));
    await userEvent.type(within(dialog).getByLabelText('Backup password'), 'right');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Open the backup' }));
    expect(await within(dialog).findByText("Demo Bar")).toBeInTheDocument();
  });

  it('shows a red banner when a backup failed or none worked for 48 hours', async () => {
    status = {
      state: 'failed',
      lastSuccessAt: null,
      message: "Last night's backup failed: disk full",
    };
    renderRoutes(routes, '/admin/settings/system');
    const banner = await screen.findByText("Last night's backup failed: disk full");
    expect(banner.closest('[role="alert"]')).toHaveClass('nx-banner--warning');
    expect(screen.getByRole('link', { name: 'Backups' })).toHaveAttribute(
      'href',
      '/admin/settings/backups',
    );
  });

  it('shows the health and installs an update only with the day closed or UPDATE typed', async () => {
    extra['GET /api/admin/system/updates'] = () =>
      json(200, {
        current: '1.0.0',
        latest: '1.1.0',
        available: true,
        notes: 'Faster reports.',
        canInstallHere: true,
        cantInstallReason: null,
        dayOpen: true,
        error: null,
      });
    extra['POST /api/admin/system/updates/apply'] = () => new Response(null, { status: 202 });
    renderRoutes(routes, '/admin/settings/system');
    const health = await screen.findByRole('region', { name: 'Health' });
    expect(await within(health).findByText('POS-SERVER')).toBeInTheDocument();
    expect(within(health).getByText('50.0 GB')).toBeInTheDocument();
    expect(within(health).getByText('Connected')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Check for updates' }));
    expect(
      await screen.findByText('Tausi POS 1.1.0 is available (this server has 1.0.0).'),
    ).toBeInTheDocument();
    expect(screen.getByText('Faster reports.')).toBeInTheDocument();
    const install = screen.getByRole('button', { name: 'Install 1.1.0' });
    expect(install).toBeDisabled();
    await userEvent.type(screen.getByLabelText('The day is open'), 'UPDATE');
    await userEvent.click(install);
    expect(await screen.findByText(/The update has started/)).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/api/admin/system/updates/apply')?.body).toEqual({
      force: true,
      confirm: 'UPDATE',
    });
  });

  it('says so over everything while the server restores a backup', async () => {
    renderRoutes(routes, '/admin/settings/system');
    await screen.findByRole('region', { name: 'Health' });
    extra['GET /api/admin/system/updates'] = () =>
      json(503, { status: 503, code: 'server_restoring', detail: 'Server is restoring a backup' });
    await act(async () => {
      await api('/api/admin/system/updates').catch(() => {});
    });
    expect(
      await screen.findByRole('alertdialog', { name: 'Server is restoring a backup' }),
    ).toBeInTheDocument();
  });
});

describe('Admin password reset', () => {
  const fetchMock = vi.fn<typeof fetch>();
  beforeEach(() => vi.stubGlobal('fetch', fetchMock));
  afterEach(() => vi.unstubAllGlobals());

  it('takes the token printed on the server and a new password', async () => {
    let tries = 0;
    fetchMock.mockImplementation(
      fakeServer({
        'POST /api/setup/recover': ({ body }) =>
          ++tries === 1
            ? json(400, {
                status: 400,
                detail: "That isn't the token `reset-admin` printed. 4 tries left.",
              })
            : json(200, { userName: (body as { userName: string | null }).userName ?? 'admin' }),
      }),
    );
    renderRoutes(routes, '/setup/recover');
    await userEvent.type(await screen.findByLabelText('Reset token'), 'k7qm-3hxp');
    expect(screen.getByLabelText('Reset token')).toHaveValue('K7QM-3HXP');
    await userEvent.type(screen.getByLabelText('New password'), 'a long new password');
    await userEvent.click(screen.getByRole('button', { name: 'Set the new password' }));
    expect(await screen.findByText(/4 tries left/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Set the new password' }));
    expect(await screen.findByRole('heading', { name: 'Password changed' })).toBeInTheDocument();
    expect(
      screen.getByText('Sign in to the admin site as admin with the new password.'),
    ).toBeInTheDocument();
  });
});
