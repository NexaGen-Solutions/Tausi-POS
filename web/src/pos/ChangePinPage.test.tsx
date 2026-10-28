import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { setDeviceForTests } from '../shared/device/device';
import { pinSession, setPinSession } from '../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../test/render';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('first login: change PIN', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const changes: string[] = [];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    changes.length = 0;
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/auth/pin/max-length': () => json(200, 4),
        'GET /api/brand': () => json(200, { name: 'Demo Bar' }),
        'POST /api/auth/pin': () =>
          json(200, sessionFor('edna', 'Waiter', { mustChangePin: true })),
        'POST /api/auth/pin/change': ({ body }) => {
          const pin = (body as { newPin: string }).newPin;
          changes.push(pin);
          return pin === '5739'
            ? json(400, { status: 400, code: 'pin_taken', detail: 'Choose a different PIN.' })
            : new Response(null, { status: 204 });
        },
      }),
    );
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  it('asks for a new PIN twice, handles a mismatch and a taken PIN, then goes in', async () => {
    renderRoutes(routes, '/pos');
    await screen.findByLabelText('0 of 4 digits entered');
    await userEvent.keyboard('4821');

    expect(await screen.findByRole('heading', { name: 'Choose a new PIN' })).toBeInTheDocument();
    expect(screen.getByText(/Hello edna/)).toBeInTheDocument();

    // Mismatch: back to the first step, nothing sent.
    await userEvent.keyboard('5739{Enter}');
    expect(await screen.findByRole('heading', { name: 'Enter it again' })).toBeInTheDocument();
    await userEvent.keyboard('5738{Enter}');
    expect(
      await screen.findByText("The PINs don't match. Choose your new PIN again."),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Choose a new PIN' })).toBeInTheDocument();
    expect(changes).toEqual([]);

    // Taken by someone else: the server says no, without saying who.
    await userEvent.keyboard('5739{Enter}');
    await screen.findByRole('heading', { name: 'Enter it again' });
    await userEvent.keyboard('5739{Enter}');
    expect(await screen.findByText('Choose a different PIN.')).toBeInTheDocument();

    // A good one.
    await userEvent.keyboard('90517{Enter}');
    await screen.findByRole('heading', { name: 'Enter it again' });
    await userEvent.keyboard('90517{Enter}');

    expect(await screen.findByRole('button', { name: 'All tables' })).toBeInTheDocument();
    expect(changes).toEqual(['5739', '90517']);
    expect(pinSession()?.mustChangePin).toBe(false);
  });

  it('cannot skip the change by going to another screen', async () => {
    setPinSession(sessionFor('edna', 'Waiter', { mustChangePin: true }));
    renderRoutes(routes, '/pos/tables');

    expect(await screen.findByRole('heading', { name: 'Choose a new PIN' })).toBeInTheDocument();
  });
});
