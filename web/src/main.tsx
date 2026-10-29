import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { Providers } from './app/Providers';
import { routes } from './app/routes';
import { setApiBase } from './shared/api/client';
import { createQueryClient } from './shared/api/queryClient';
import { isBundledTerminalPage, loadDevice } from './shared/device/device';
import { StartFailed } from './app/StartFailed';
import './shared/ui/fonts';
import './shared/ui/tokens.css';
import './shared/ui/base.css';
import './shared/ui/ui.css';

const root = createRoot(document.getElementById('root')!);
try {
  const device = await loadDevice();
  // The terminal app's bundled setup screen talks to the server it was given; everything else is same-origin.
  if (isBundledTerminalPage() && device.serverUrl) setApiBase(device.serverUrl);

  const router = createBrowserRouter(routes);
  const queryClient = createQueryClient();

  root.render(
    <StrictMode>
      <Providers client={queryClient}>
        <RouterProvider router={router} />
      </Providers>
    </StrictMode>,
  );
} catch (error) {
  // Never a blank screen: say what failed, so whoever is at the counter can tell NexaGen.
  root.render(<StartFailed error={error} />);
}
