import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('Settings → Training mode', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let status: {
    terminals: { id: string; name: string; training: boolean; rowVersion: number }[];
    tickets: number;
    openTickets: number;
  };

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    status = {
      terminals: [
        { id: 't1', name: 'Counter A', training: false, rowVersion: 3 },
        { id: 't2', name: 'Pillar 1', training: true, rowVersion: 5 },
      ],
      tickets: 4,
      openTickets: 1,
    };
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
      });
      return fakeServer({
        'GET /api/auth/me': () =>
          json(200, {
            user: { id: 'a', name: 'Administrator', role: 'Admin' },
            permissions: { 'settings.manage': 'Allowed' },
          }),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', logoFileId: null, currency: 'KES' }),
        'GET /api/licence': () => json(200, { banner: null, status: 'Active' }),
        'GET /api/backups/status': () =>
          json(200, { state: 'ok', lastSuccessAt: null, message: null }),
        'GET /api/admin/training': () => json(200, status),
        'PUT /api/admin/training/terminals/t1': () => {
          status.terminals[0] = { ...status.terminals[0], training: true, rowVersion: 4 };
          return json(200, status.terminals[0]);
        },
        'POST /api/admin/training/clear': () => {
          status = { ...status, tickets: 0, openTickets: 0 };
          return json(200, { tickets: 4 });
        },
      })(input, init);
    });
  });
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('switches training on for a terminal with its version', async () => {
    renderRoutes(routes, '/admin/settings/training');
    const counterA = await screen.findByRole('switch', { name: 'Training mode on Counter A' });
    expect(counterA).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByRole('switch', { name: 'Training mode on Pillar 1' })).toHaveAttribute(
      'aria-checked',
      'true',
    );

    await userEvent.click(counterA);
    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'Training mode on Counter A' })).toHaveAttribute(
        'aria-checked',
        'true',
      ),
    );
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ training: true, rowVersion: 3 });
  });

  it('clears training data after asking', async () => {
    renderRoutes(routes, '/admin/settings/training');
    expect(await screen.findByText('4 training tickets (1 still open).')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Clear training data' }));
    const dialog = screen.getByRole('dialog', { name: 'Clear training data?' });
    expect(calls.some((c) => c.method === 'POST')).toBe(false);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Clear training data' }));

    expect(await screen.findByText('Training data cleared: 4 tickets.')).toBeInTheDocument();
    expect(await screen.findByText('There is no training data.')).toBeInTheDocument();
  });
});
