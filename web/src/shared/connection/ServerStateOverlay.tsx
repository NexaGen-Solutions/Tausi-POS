import { useEffect, useState } from 'react';
import { t } from '../i18n/i18n';
import { checkForTerminalUpdate, installTerminalUpdate, isTauri } from '../device/device';
import {
  onServerMode,
  serverMode,
  serverVersion,
  setServerMode,
  type ServerMode,
} from './serverState';

/**
 * Over everything while the server restores a backup ("Server is restoring a backup"; it checks every
 * few seconds and reloads when the server is back), or while this terminal app updates itself
 * because the server needs a newer one ("Updating, please wait").
 */
export function ServerStateOverlay() {
  const [mode, setMode] = useState<ServerMode>(serverMode());
  useEffect(() => onServerMode(setMode), []);

  useEffect(() => {
    if (mode !== 'restoring') return;
    const timer = window.setInterval(() => {
      // The brand answers 503 for as long as the restore runs (health and the backup status don't).
      void fetch('/api/brand', { credentials: 'same-origin' })
        .then((r) => {
          if (r.status !== 503) {
            setServerMode('normal');
            location.reload();
          }
        })
        .catch(() => {});
    }, 5000);
    return () => window.clearInterval(timer);
  }, [mode]);

  // Too old for the server: fetch the matching terminal version from it, and install it now (no one is logged in).
  useEffect(() => {
    if (mode !== 'updating' || !isTauri()) return;
    const server = serverVersion();
    if (!server) return;
    void checkForTerminalUpdate(server)
      .then((ready) => (ready ? installTerminalUpdate() : undefined))
      .catch(() => {});
  }, [mode]);

  if (mode === 'normal') return null;
  return (
    <div
      className="server-overlay"
      role="alertdialog"
      aria-live="assertive"
      aria-label={t(`server.${mode}`)}
    >
      <div className="server-overlay__box">
        <p className="server-overlay__title">{t(`server.${mode}`)}</p>
        <p>{t(`server.${mode}Help`)}</p>
      </div>
    </div>
  );
}
