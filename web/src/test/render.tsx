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
