import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useState } from 'react';
import { Navigate, useNavigate } from 'react-router';
import { ApiError, api, apiUrl } from '../shared/api/client';
import type { DeviceInfo } from '../shared/api/types';
import { OfflineBanner } from '../shared/connection/OfflineBanner';
import { isOffline } from '../shared/connection/status';
import { useConnection } from '../shared/connection/useConnection';
import { deviceToken, saveDevice, serverUrl } from '../shared/device/device';
import { t } from '../shared/i18n/i18n';
import { setPinSession, type PinSession } from '../shared/session/session';
import { Keypad, PinDots } from '../shared/ui';
import './pos.css';

const maxPin = 6;

/** Wireframe 1. The full screen (work period, styling details) is finished in the Screen 1 task. */
export function PinLoginPage() {
  const navigate = useNavigate();
  const connection = useConnection();
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [lockedFor, setLockedFor] = useState(0);

  const device = useQuery({
    queryKey: ['device'],
    queryFn: () => api<DeviceInfo>('/api/device'),
    enabled: !!deviceToken(),
  });

  useEffect(() => {
    if (lockedFor <= 0) return;
    const timer = window.setTimeout(() => setLockedFor((s) => s - 1), 1000);
    return () => window.clearTimeout(timer);
  }, [lockedFor]);

  const submit = useCallback(async () => {
    if (pin.length < 4 || busy || lockedFor > 0) return;
    setBusy(true);
    setError(null);
    try {
      const session = await api<PinSession>('/api/auth/pin', { method: 'POST', body: { pin } });
      setPinSession(session);
      navigate('/pos/home', { replace: true });
    } catch (e) {
      setPin('');
      if (e instanceof ApiError && e.status === 429) {
        setLockedFor(e.problem.retryAfterSeconds ?? 60);
      } else if (e instanceof ApiError && e.status === 401) {
        const left = e.problem.attemptsLeft;
        setError(left ? t('pos.wrongPinLeft', { count: left }) : t('pos.wrongPin'));
      } else {
        setError(t('common.somethingWrong'));
      }
    } finally {
      setBusy(false);
    }
  }, [busy, lockedFor, navigate, pin]);

  // Physical keyboards work too.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (/^\d$/.test(event.key)) setPin((p) => (p.length < maxPin ? p + event.key : p));
      else if (event.key === 'Backspace') setPin((p) => p.slice(0, -1));
      else if (event.key === 'Escape') setPin('');
      else if (event.key === 'Enter') void submit();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [submit]);

  if (!deviceToken()) return <Navigate to="/setup" replace />;
  if (device.error instanceof ApiError && device.error.status === 401) {
    // The server no longer knows this device token (re-paired elsewhere): pair again.
    void saveDevice({ serverUrl: serverUrl(), deviceToken: null });
    return <Navigate to="/setup" replace />;
  }

  const offline = isOffline(connection);
  const message = lockedFor > 0 ? t('pos.locked', { seconds: lockedFor }) : error;

  return (
    <div className="pos-screen">
      <OfflineBanner />
      <div className="pin-login">
        <aside className="pin-login__side">
          <p className="pin-login__label">{t('pos.terminalLabel')}</p>
          <p className="pin-login__terminal">{device.data?.terminalName ?? ''}</p>
          <div className="pin-login__brand">
            {device.data?.logoFileId && (
              <img
                className="pin-login__logo"
                src={apiUrl(`/files/${device.data.logoFileId}`)}
                alt=""
              />
            )}
            <p className="pin-login__label">{t('common.appName')}</p>
            <p className="pin-login__business">{device.data?.businessName ?? ''}</p>
          </div>
          <p className="pin-login__status">
            <span
              className={`pin-login__status-dot${offline ? ' pin-login__status-dot--off' : ''}`}
              aria-hidden="true"
            />
            {offline
              ? t('connection.offline')
              : t('connection.connectedTo', { server: connection.serverName ?? '' })}
          </p>
        </aside>
        <main className="pin-login__pad">
          <h1 className="pin-login__title">{t('pos.enterPin')}</h1>
          <PinDots length={pin.length} />
          <Keypad
            onDigit={(d) => setPin((p) => (p.length < maxPin ? p + d : p))}
            onClear={() => setPin('')}
            onEnter={() => void submit()}
            enterDisabled={pin.length < 4 || busy || lockedFor > 0}
          />
          <p className="pin-login__error" role="alert">
            {message}
          </p>
          <p className="pin-login__help">{t('pos.pinHelp')}</p>
        </main>
      </div>
    </div>
  );
}
