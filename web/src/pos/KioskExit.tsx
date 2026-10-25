import { useRef, useState } from 'react';
import { ApiError, api } from '../shared/api/client';
import { exitKiosk, kioskMode } from '../shared/device/device';
import { t } from '../shared/i18n/i18n';
import { Button, Field, Modal } from '../shared/ui';

export const TAPS = 5;
export const WITHIN = 3000;

/**
 * Leaving kiosk mode: five quick taps in the top-left corner (nothing shows there), then an
 * admin-site login. Cashiers can't leave the POS; a technician can, and can switch kiosk mode off
 * on this PC.
 */
export function KioskExit() {
  const taps = useRef<number[]>([]);
  const [open, setOpen] = useState(false);
  if (!kioskMode()) return null;
  const tap = () => {
    const now = Date.now();
    taps.current = [...taps.current.filter((at) => now - at < WITHIN), now];
    if (taps.current.length >= TAPS) {
      taps.current = [];
      setOpen(true);
    }
  };
  return (
    <>
      <button
        type="button"
        className="kiosk-corner"
        aria-label={t('pos.kiosk.corner')}
        onClick={tap}
      />
      {open && <KioskExitDialog onClose={() => setOpen(false)} />}
    </>
  );
}

export function KioskExitDialog({ onClose }: { onClose: () => void }) {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [turnOff, setTurnOff] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const leave = async () => {
    setBusy(true);
    setError(null);
    try {
      await api('/api/terminals/kiosk-exit', { method: 'POST', body: { userName, password } });
      await exitKiosk(turnOff);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
      setBusy(false);
    }
  };
  return (
    <Modal
      title={t('pos.kiosk.title')}
      subtitle={t('pos.kiosk.help')}
      onClose={onClose}
      width={480}
    >
      <form
        className="kiosk-exit"
        onSubmit={(e) => {
          e.preventDefault();
          void leave();
        }}
      >
        <Field
          label={t('pos.kiosk.userName')}
          value={userName}
          autoComplete="off"
          onChange={(e) => setUserName(e.target.value)}
        />
        <Field
          label={t('pos.kiosk.password')}
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={error}
        />
        <label className="kiosk-exit__off">
          <input type="checkbox" checked={turnOff} onChange={(e) => setTurnOff(e.target.checked)} />
          {t('pos.kiosk.turnOff')}
        </label>
        <div className="kiosk-exit__buttons">
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button type="submit" variant="primary" disabled={busy || !userName || !password}>
            {t('pos.kiosk.exit')}
          </Button>
        </div>
      </form>
    </Modal>
  );
}
