import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal } from '../../shared/ui';

/**
 * Moving to a new server PC (after restoring this business's backup there): every running terminal
 * goes to the new address and stays paired.
 */
export function MoveDialog({ onClose }: { onClose: () => void }) {
  const [address, setAddress] = useState('http://');
  const [error, setError] = useState<string | null>(null);
  const [moved, setMoved] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const move = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ serverUrl: string }>('/api/admin/terminals/move', {
        method: 'POST',
        body: { serverUrl: address },
      });
      setMoved(r.serverUrl);
    } catch (e) {
      setError(
        e instanceof ApiError
          ? (e.problem.fieldErrors?.serverUrl ?? e.message)
          : t('common.somethingWrong'),
      );
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal
      title={t('admin.devices.move.title')}
      subtitle={t('admin.devices.move.help')}
      onClose={onClose}
      width={520}
    >
      {moved ? (
        <div className="move">
          <p role="status">{t('admin.devices.move.done', { address: moved })}</p>
          <div className="move__buttons">
            <Button variant="primary" onClick={onClose}>
              {t('common.close')}
            </Button>
          </div>
        </div>
      ) : (
        <form
          className="move"
          onSubmit={(e) => {
            e.preventDefault();
            void move();
          }}
        >
          <Field
            label={t('admin.devices.move.address')}
            value={address}
            inputMode="url"
            onChange={(e) => setAddress(e.target.value)}
            error={error}
          />
          <div className="move__buttons">
            <Button onClick={onClose}>{t('common.cancel')}</Button>
            <Button type="submit" variant="primary" disabled={busy}>
              {t('admin.devices.move.submit')}
            </Button>
          </div>
        </form>
      )}
    </Modal>
  );
}
