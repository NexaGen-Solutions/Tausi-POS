import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Navigate, useNavigate } from 'react-router';
import { ApiError, OfflineError, api, apiUrl } from '../shared/api/client';
import type { DeviceInfo } from '../shared/api/types';
import { TausiMark } from '../shared/brand';
import { isOffline } from '../shared/connection/status';
import { useConnection } from '../shared/connection/useConnection';
import { deviceToken, saveDevice, serverUrl } from '../shared/device/device';
import { t } from '../shared/i18n/i18n';
import { setPinSession, takeLogoutNotice, type PinSession } from '../shared/session/session';
import { PinPad } from '../shared/ui';

const wrongPinFor = 3000;
const noticeFor = 5000;

function formatCountdown(seconds: number) {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function formatTime(iso: string) {
  return new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
}

/** Wireframe 1: the PIN pad every terminal shows when nobody is logged in. */
export function PinLoginPage() {
  const navigate = useNavigate();
  const connection = useConnection();
  const [pin, setPin] = useState('');
  const [busy, setBusy] = useState(false);
  const [shake, setShake] = useState(0);
  const [wrongUntil, setWrongUntil] = useState(0);
  const [lockedUntil, setLockedUntil] = useState(0);
  const [now, setNow] = useState(() => Date.now());
  const [notice, setNotice] = useState(() => {
    const ended = takeLogoutNotice();
    return ended?.reason === 'idle'
      ? t('pos.loggedOutIdle', { minutes: ended.idleLogoutMinutes ?? 0 })
      : null;
  });

  const paired = !!deviceToken();
  const device = useQuery({
    queryKey: ['device'],
    queryFn: () => api<DeviceInfo>('/api/device'),
    enabled: paired,
  });
  const maxLength = useQuery({
    queryKey: ['pin-max-length'],
    queryFn: () => api<number>('/api/auth/pin/max-length'),
    enabled: paired,
  });

  // One clock for the countdown and the timed messages.
  const ticking = lockedUntil > now || wrongUntil > now;
  useEffect(() => {
    if (!ticking) return;
    const timer = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(timer);
  }, [ticking]);

  useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(null), noticeFor);
    return () => window.clearTimeout(timer);
  }, [notice]);

  if (!paired) return <Navigate to="/connect" replace />;
  if (device.error instanceof ApiError && device.error.status === 401) {
    // The server no longer knows this device token (re-paired elsewhere): pair again.
    void saveDevice({ serverUrl: serverUrl(), deviceToken: null });
    return <Navigate to="/connect" replace />;
  }

  const offline = isOffline(connection);
  const lockSeconds = Math.max(0, Math.ceil((lockedUntil - now) / 1000));
  const locked = lockSeconds > 0;

  const submit = async (value: string) => {
    if (busy || locked || offline) return;
    setBusy(true);
    try {
      const session = await api<PinSession>('/api/auth/pin', {
        method: 'POST',
        body: { pin: value },
      });
      setPinSession(session);
      navigate(session.mustChangePin ? '/pos/change-pin' : '/pos/tables', { replace: true });
    } catch (error) {
      setPin('');
      const at = Date.now();
      setNow(at);
      if (error instanceof ApiError && error.status === 429) {
        setLockedUntil(at + (error.problem.retryAfterSeconds ?? 60) * 1000);
      } else if (!(error instanceof OfflineError)) {
        setShake((n) => n + 1);
        setWrongUntil(at + wrongPinFor);
      }
    } finally {
      setBusy(false);
    }
  };

  let message: string | null;
  if (offline) message = t('pos.offline');
  else if (locked) message = t('pos.locked', { time: formatCountdown(lockSeconds) });
  else if (wrongUntil > now) message = t('pos.pinNotRecognised');
  else message = notice;

  const period = device.data?.workPeriod;

  return (
    <div className="pos-screen">
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
            <p className="pin-login__label pin-login__tausi">
              <TausiMark size={36} label={null} />
              {t('common.appName')}
            </p>
            <p className="pin-login__business">{device.data?.businessName ?? ''}</p>
            {device.data && (
              <p className="pin-login__day">
                {period
                  ? t('pos.dayOpenSince', { time: formatTime(period.openedAt) })
                  : t('pos.dayNotOpen')}
              </p>
            )}
          </div>
          <p className="pin-login__status">
            <span
              className={`pin-login__status-dot${offline ? ' pin-login__status-dot--off' : ''}`}
              aria-hidden="true"
            />
            {offline
              ? t('pos.reconnecting')
              : t('pos.connectedTo', { server: connection.serverName ?? serverUrl() ?? '' })}
          </p>
        </aside>
        <main className="pin-login__pad">
          <h1 className="pin-login__title">{t('pos.enterPin')}</h1>
          <PinPad
            value={pin}
            onChange={setPin}
            onSubmit={(value) => void submit(value)}
            autoSubmitLength={maxLength.data}
            slots={maxLength.data ?? 4}
            disabled={busy || locked || offline}
            shakeKey={shake}
            keepFocus
          />
          <p className="pin-login__message" role="alert">
            {message}
          </p>
          <p className="pin-login__help">{t('pos.pinHelp')}</p>
        </main>
      </div>
    </div>
  );
}
