import { screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { json, renderRoutes } from '../test/render';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('admin layout', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => vi.stubGlobal('fetch', fetchMock));
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('shows the full sidebar with the business name and logo from settings', async () => {
    fetchMock.mockImplementation(async (url) => {
      const path = String(url);
      if (path.endsWith('/api/auth/me'))
        return json(200, {
          user: { id: '1', name: 'Administrator', role: 'Admin' },
          permissions: {},
        });
      if (path.endsWith('/api/brand'))
        return json(200, { name: 'Demo Bar', logoFileId: 'logo-1', currency: 'KES' });
      return json(200, { server: 'POS-SERVER' });
    });

    renderRoutes(routes, '/admin/erp');

    expect(await screen.findByText('Demo Bar')).toBeInTheDocument();
    expect(screen.getByText('Tausi POS admin')).toBeInTheDocument();
    expect(
      screen.getByRole('link', {
        name: /^Tausi POS · by NexaGen Solutions · version \d+\.\d+\.\d+$/,
      }),
    ).toHaveAttribute('href', '/admin/about');
    expect(screen.getByRole('img', { name: 'Logo' })).toHaveAttribute('src', '/files/logo-1');
    const nav = screen.getByRole('navigation', { name: 'Admin' });
    const links = within(within(nav).getByRole('list'))
      .getAllByRole('link')
      .map((a) => a.textContent);
    expect(links).toEqual([
      'Menu and prices',
      'Stock and recipes',
      'Deliveries',
      'Stock counts',
      'Cash book',
      'Staff and PINs',
      'Permissions',
      'Terminals and printers',
      'Tables',
      'Reports',
      'Settings',
      'ERPNext sync',
    ]);
    expect(within(nav).getByRole('link', { name: 'ERPNext sync' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(screen.getByRole('heading', { name: 'ERPNext sync' })).toBeInTheDocument();
    expect(screen.getByText('Coming in a later version.')).toBeInTheDocument();
    expect(await screen.findByText('Signed in: Administrator')).toBeInTheDocument();
  });

  it('sends signed-out visitors to the sign-in page', async () => {
    // Signed out: everything that needs a session answers 401, as the server does.
    fetchMock.mockImplementation(async (url) =>
      /\/api\/(auth\/me|admin\/)/.test(String(url)) ? json(401, { status: 401 }) : json(200, {}),
    );

    renderRoutes(routes, '/admin/menu');

    expect(await screen.findByRole('heading', { name: 'Admin sign in' })).toBeInTheDocument();
  });
});
