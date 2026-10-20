import { act, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { setDeviceForTests } from '../shared/device/device';
import { setPinSession } from '../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../test/render';

const hubHandlers = new Map<string, (payload: unknown) => void>();
vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn((name: string, handler: (payload: unknown) => void) => {
    hubHandlers.set(name, handler);
    return () => hubHandlers.delete(name);
  }),
}));

describe('terminal test menu (temporary)', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let tuskerPrice = 250;

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    tuskerPrice = 250;
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/brand': () => json(200, { name: "Mau's Village" }),
        'GET /api/pos/menu': () =>
          json(200, {
            menuVersion: String(tuskerPrice),
            priceListName: 'Standard',
            categories: [
              {
                id: 'c',
                name: 'Beers',
                items: [
                  {
                    id: 'i',
                    name: 'Tusker Lager',
                    shortName: 'Tusker Lager',
                    price: tuskerPrice,
                    groups: [],
                  },
                ],
              },
            ],
          }),
      }),
    );
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  it('shows the menu and updates after menu.changed', async () => {
    setPinSession(sessionFor('edna', 'Waiter'));
    renderRoutes(routes, '/pos/tables');

    const list = await screen.findByRole('region', { name: 'Menu (for testing)' });
    expect(await within(list).findByTestId('price-Tusker Lager')).toHaveTextContent('250');

    tuskerPrice = 260;
    act(() => hubHandlers.get('menu.changed')?.({}));

    expect(await within(list).findByText('260')).toBeInTheDocument();
  });
});
