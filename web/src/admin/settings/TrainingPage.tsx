import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, Toggle } from '../../shared/ui';
import '../stock/stock.css';
import './training.css';

interface TrainingTerminal {
  id: string;
  name: string;
  training: boolean;
  rowVersion: number;
}

interface TrainingStatus {
  terminals: TrainingTerminal[];
  tickets: number;
  openTickets: number;
}

/**
 * Settings → Training mode: training on or off per terminal, and Clear training data. A training
 * terminal shows an orange band, prints TRAINING on every slip, and none of it reaches the real figures.
 */
export function TrainingPage() {
  const queries = useQueryClient();
  const status = useQuery({
    queryKey: ['training'],
    queryFn: () => api<TrainingStatus>('/api/admin/training'),
  });
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      await action();
      await queries.invalidateQueries({ queryKey: ['training'] });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const toggle = (terminal: TrainingTerminal, training: boolean) =>
    run(async () => {
      await api(`/api/admin/training/terminals/${terminal.id}`, {
        method: 'PUT',
        body: { training, rowVersion: terminal.rowVersion },
      });
    });

  const clear = () =>
    run(async () => {
      const cleared = await api<{ tickets: number }>('/api/admin/training/clear', {
        method: 'POST',
      });
      setConfirming(false);
      setMessage(t('admin.training.cleared', { tickets: cleared.tickets }));
    });

  const s = status.data;
  return (
    <main className="stock stock--single">
      <div className="stock__main training">
        <h1 className="admin__title">{t('admin.training.title')}</h1>
        <p className="training__help">{t('admin.training.help')}</p>
        <section className="training__panel" aria-label={t('admin.training.terminals')}>
          <h2 className="training__heading">{t('admin.training.terminals')}</h2>
          <ul className="training__list">
            {s?.terminals.map((terminal) => (
              <li key={terminal.id} className="training__row">
                <span>{terminal.name}</span>
                <Toggle
                  label={t('admin.training.switch', { terminal: terminal.name })}
                  checked={terminal.training}
                  disabled={busy}
                  onChange={(on) => void toggle(terminal, on)}
                />
              </li>
            ))}
          </ul>
        </section>
        <section className="training__panel" aria-label={t('admin.training.data')}>
          <h2 className="training__heading">{t('admin.training.data')}</h2>
          <p>
            {s && s.tickets > 0
              ? t('admin.training.count', { tickets: s.tickets, open: s.openTickets })
              : t('admin.training.none')}
          </p>
          <div>
            <Button
              variant="danger"
              disabled={busy || !s || s.tickets === 0}
              onClick={() => setConfirming(true)}
            >
              {t('admin.training.clear')}
            </Button>
          </div>
        </section>
        {message && <p role="status">{message}</p>}
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
      </div>
      {confirming && (
        <Modal
          title={t('admin.training.clearTitle')}
          onClose={busy ? undefined : () => setConfirming(false)}
          width={520}
        >
          <p>{t('admin.training.clearText')}</p>
          <div className="training__actions">
            <Button onClick={() => setConfirming(false)} disabled={busy}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" onClick={() => void clear()} disabled={busy}>
              {t('admin.training.clear')}
            </Button>
          </div>
        </Modal>
      )}
    </main>
  );
}
