import { useEffect } from 'react';
import { api } from '../shared/api/client';
import { endPinSession, idleDeadline, lastSessionTouch } from '../shared/session/session';

/** Touching the screen keeps the session alive, but at most one keep-alive per 30 seconds. */
const keepAliveEvery = 30_000;

/**
 * Logs out after the business's idle time, at the same moment the server does: both count from
 * the last request the server saw. Taps and key presses send a keep-alive so a user who is
 * working without saving anything is not logged out.
 */
export function IdleWatcher() {
  useEffect(() => {
    const timer = window.setInterval(() => {
      const deadline = idleDeadline();
      if (deadline !== null && Date.now() >= deadline) endPinSession('idle');
    }, 1000);

    const onActivity = () => {
      const deadline = idleDeadline();
      if (deadline === null || Date.now() >= deadline) return;
      if (Date.now() - lastSessionTouch() >= keepAliveEvery) {
        api('/api/auth/session').catch(() => {});
      }
    };
    window.addEventListener('pointerdown', onActivity);
    window.addEventListener('keydown', onActivity);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener('pointerdown', onActivity);
      window.removeEventListener('keydown', onActivity);
    };
  }, []);

  return null;
}
