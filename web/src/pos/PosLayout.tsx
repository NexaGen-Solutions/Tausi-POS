import { useQueryClient } from '@tanstack/react-query';
import { ApiError, api } from '../shared/api/client';
import { saveDevice, serverUrl } from '../shared/device/device';
import { useEffect } from 'react';
import { Outlet, useNavigate } from 'react-router';
import { LicenceBanner } from '../shared/connection/LicenceBanner';
import { onPosEvent } from '../shared/connection/hub';
import { LiveUpdates } from '../shared/connection/LiveUpdates';
import { ServerStateOverlay } from '../shared/connection/ServerStateOverlay';
import { endPinSession, onPinSessionChange, pinSession } from '../shared/session/session';
import { ApprovalsWatcher } from './approvals/ApprovalsWatcher';
import { IdleWatcher } from './IdleWatcher';
import { KioskExit } from './KioskExit';
import { TerminalUpdater } from './TerminalUpdater';
import { usePinSession } from './usePinSession';
import './pos.css';

/**
 * Wraps every terminal screen: keeps the live connection open (also on the PIN pad, so it updates
 * when the day opens), watches for idle time, leaves at once when a manager deactivates the user
 * or resets their PIN, and when a session ends for any reason clears
 * everything cached and returns to the PIN pad, so nothing of the previous user stays on screen.
 */
export function PosLayout() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const session = usePinSession();

  useEffect(
    () =>
      onPinSessionChange((session) => {
        if (session) return;
        queries.clear();
        navigate('/pos', { replace: true });
      }),
    [navigate, queries],
  );

  // Deactivated, or PIN reset by a manager: the server has already ended the session; leave now.
  useEffect(() => {
    const leaveIfMe = (payload: unknown) => {
      const userId = (payload as { userId?: string } | null)?.userId;
      if (userId && pinSession()?.user.id === userId) endPinSession('expired');
    };
    // An admin revoked a terminal: if it was this one, its device token no longer works. Back to pairing.
    const checkRevoked = async () => {
      try {
        await api('/api/device');
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) {
          endPinSession('expired');
          await saveDevice({ serverUrl: serverUrl(), deviceToken: null });
          queries.clear();
          navigate('/connect?revoked=1', { replace: true });
        }
      }
    };
    const off = [
      onPosEvent('user.deactivated', leaveIfMe),
      onPosEvent('user.pin_reset', leaveIfMe),
      onPosEvent('terminal.revoked', () => void checkRevoked()),
    ];
    return () => off.forEach((unsubscribe) => unsubscribe());
  }, [navigate, queries]);

  return (
    <>
      <LiveUpdates />
      <IdleWatcher />
      <TerminalUpdater />
      <KioskExit />
      <ServerStateOverlay />
      {session && <ApprovalsWatcher />}
      {session && <LicenceBanner admin={false} />}
      <Outlet />
    </>
  );
}
