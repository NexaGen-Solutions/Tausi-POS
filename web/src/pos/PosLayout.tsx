import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { Outlet, useNavigate } from 'react-router';
import { LiveUpdates } from '../shared/connection/LiveUpdates';
import { onPinSessionChange } from '../shared/session/session';
import { IdleWatcher } from './IdleWatcher';
import './pos.css';

/**
 * Wraps every terminal screen: keeps the live connection open (also on the PIN pad, so it updates
 * when the day opens), watches for idle time, and when a session ends for any reason clears
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

  return (
    <>
      <LiveUpdates />
      <IdleWatcher />
      <Outlet />
    </>
  );
}
