import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { Providers } from './app/Providers';
import { routes } from './app/routes';
import { setApiBase } from './shared/api/client';
import { createQueryClient } from './shared/api/queryClient';
import { isBundledTerminalPage, loadDevice } from './shared/device/device';
import './shared/ui/fonts';
import './shared/ui/tokens.css';
import './shared/ui/base.css';
import './shared/ui/ui.css';

const device = await loadDevice();
// The terminal app's bundled setup screen talks to the server it was given; everything else is same-origin.
if (isBundledTerminalPage() && device.serverUrl) setApiBase(device.serverUrl);

const router = createBrowserRouter(routes);
const queryClient = createQueryClient();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Providers client={queryClient}>
      <RouterProvider router={router} />
    </Providers>
  </StrictMode>,
);
