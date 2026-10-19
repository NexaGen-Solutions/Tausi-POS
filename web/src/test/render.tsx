import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router';
import { Providers } from '../app/Providers';
import { createQueryClient } from '../shared/api/queryClient';

/** Renders routes inside the app's providers, starting at a given URL. */
export function renderRoutes(routes: RouteObject[], url: string) {
  const router = createMemoryRouter(routes, { initialEntries: [url] });
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { retry: false } });
  render(
    <Providers client={client}>
      <RouterProvider router={router} />
    </Providers>,
  );
  return router;
}

export function renderWithProviders(element: ReactElement) {
  return renderRoutes([{ path: '/', element }], '/');
}

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

type Handler = (request: { body: unknown; headers: Headers }) => Response | Promise<Response>;

/**
 * A fake server for fetch: handlers keyed by "METHOD /path". Unknown paths answer 404.
 * /api/health answers by default, as the connection watcher calls it.
 */
export function fakeServer(handlers: Record<string, Handler>) {
  return async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    const method = (init?.method ?? 'GET').toUpperCase();
    const handler =
      handlers[`${method} ${url.pathname}`] ??
      (url.pathname === '/api/health' ? () => json(200, { server: 'POSSERVER2' }) : undefined);
    if (!handler) return json(404, { status: 404 });
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    return handler({ body, headers: new Headers(init?.headers) });
  };
}

export const sessionFor = (
  name: string,
  role: string,
  extra: { mustChangePin?: boolean; idleLogoutMinutes?: number } = {},
) => ({
  token: `token-${name}`,
  expiresAt: new Date(Date.now() + 12 * 3600_000).toISOString(),
  idleLogoutMinutes: extra.idleLogoutMinutes ?? 10,
  mustChangePin: extra.mustChangePin ?? false,
  user: { id: `id-${name}`, name, role, terminalId: 't1', terminalName: 'Counter A' },
});

export const deviceInfo = (workPeriod: { openedAt: string } | null = null) => ({
  terminalId: 't1',
  terminalName: 'Counter A',
  businessName: "Mau's Village",
  logoFileId: null,
  workPeriod: workPeriod && { id: 'wp1', openedByUserId: 'u', ...workPeriod },
});
