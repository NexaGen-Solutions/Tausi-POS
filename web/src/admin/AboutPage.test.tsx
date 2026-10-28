import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { json, renderRoutes } from '../test/render';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('About Tausi POS', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => vi.stubGlobal('fetch', fetchMock));
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('opens from the admin footer: logo, version, publisher, licence and notices', async () => {
    fetchMock.mockImplementation(async (url) => {
      const path = String(url);
      if (path.endsWith('/api/auth/me'))
        return json(200, {
          user: { id: '1', name: 'Administrator', role: 'Admin' },
          permissions: {},
        });
      if (path.endsWith('/api/brand'))
        return json(200, { name: 'Demo Bar', logoFileId: null, currency: 'KES' });
      if (path.endsWith('/api/licence'))
        return json(200, { status: 'Active', edition: 'Standard', business: 'Demo Bar' });
      if (path.endsWith('/third-party-notices.txt'))
        return new Response('THIRD-PARTY NOTICES\nreact (MIT)', { status: 200 });
      return json(200, {});
    });

    renderRoutes(routes, '/admin/erp');
    await userEvent.click(
      await screen.findByRole('link', { name: /Tausi POS · by NexaGen Solutions · version/ }),
    );

    expect(await screen.findByRole('heading', { name: 'About Tausi POS' })).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Tausi POS' })).toBeInTheDocument();
    expect(screen.getByText(/^Version \d+\.\d+\.\d+$/)).toBeInTheDocument();
    expect(screen.getByText('© NexaGen Solutions')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'nexagensolutions.com' })).toHaveAttribute(
      'href',
      'https://nexagensolutions.com',
    );
    expect(await screen.findByText('Active · Standard · Demo Bar')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Third-party notices' }));
    expect(await screen.findByText(/react \(MIT\)/)).toBeInTheDocument();
  });

  it('works on the terminal before pairing, without a session', async () => {
    fetchMock.mockImplementation(async (url) =>
      String(url).endsWith('/api/licence') ? json(401, {}) : json(200, {}),
    );

    renderRoutes(routes, '/about');

    expect(await screen.findByRole('heading', { name: 'About Tausi POS' })).toBeInTheDocument();
    expect(
      await screen.findByText('Sign in to the admin site to see the licence (Settings → Licence).'),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back' })).toHaveAttribute('href', '/connect');
  });
});
