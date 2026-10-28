import { useEffect, useState } from 'react';
import { Navigate } from 'react-router';
import { TausiLogo } from '../shared/brand';
import { deviceToken, serverUrl } from '../shared/device/device';
import { t } from '../shared/i18n/i18n';
import './pos.css';

/** How often the splash asks the server whether it is up. */
const retryEvery = 2000;

const goTo = (url: string) => window.location.replace(url);

/**
 * The terminal app's first screen once paired (bundled in the app): the Tausi logo on deep teal while
 * it reaches the server, then the server's own POS screens. Keeps trying while the server is down.
 */
export function SplashPage({ open = goTo }: { open?: (url: string) => void }) {
  const server = serverUrl();
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!server) return;
    let stopped = false;
    let timer: number | undefined;
    const check = async () => {
      try {
        const response = await fetch(`${server}/api/health`);
        if (!response.ok) throw new Error();
        if (!stopped) open(`${server}/pos`);
        return;
      } catch {
        if (stopped) return;
        setFailed(true);
      }
      timer = window.setTimeout(() => void check(), retryEvery);
    };
    void check();
    return () => {
      stopped = true;
      window.clearTimeout(timer);
    };
  }, [server, open]);

  if (!server || !deviceToken()) return <Navigate to="/connect" replace />;
  const host = server.replace(/^https?:\/\//, '');

  return (
    <main className="splash">
      <div className="splash__logo">
        <TausiLogo width={720} />
      </div>
      <p className="splash__status" role="status">
        {t(failed ? 'splash.failed' : 'splash.connecting', { server: host })}
      </p>
    </main>
  );
}
