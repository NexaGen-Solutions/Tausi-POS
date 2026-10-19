import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { Outlet, useNavigate } from 'react-router';
import { onPosEvent } from '../shared/connection/hub';
import { LiveUpdates } from '../shared/connection/LiveUpdates';
import { endPinSession, onPinSessionChange, pinSession } from '../shared/session/session';
import { IdleWatcher } from './IdleWatcher';
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
    const off = [
      onPosEvent('user.deactivated', leaveIfMe),
      onPosEvent('user.pin_reset', leaveIfMe),
    ];
    return () => off.forEach((unsubscribe) => unsubscribe());
  }, []);

  return (
    <>
      <LiveUpdates />
      <IdleWatcher />
      <Outlet />
    </>
  );
}
