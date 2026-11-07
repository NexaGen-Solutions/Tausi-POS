import { useEffect, useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import { formatAmount, formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { Prompt } from './types';
import './mpesa.css';

const POLL_MS = 2000;

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });

/** mm:ss since the prompt was sent. */
function elapsed(from: string, now: number) {
  const seconds = Math.max(0, Math.floor((now - new Date(from).getTime()) / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

/**
 * Board 23: waiting for the customer's PIN (a timer, Send again, Cancel), then paid (the code, who
 * paid, when, who brought it) or what went wrong in plain words. It asks the server every 2 seconds
 * (the server asks Daraja every 5), and at once when the relay's answer arrives.
 */
export function PromptStatus({
  prompt,
  currency,
  onChange,
  onSendAgain,
  onDone,
  children,
}: {
  prompt: Prompt;
  currency: string;
  onChange: (prompt: Prompt) => void;
  onSendAgain: () => void;
  onDone: () => void;
  /** Shown under a paid prompt (the settle screen's Brought by). */
  children?: React.ReactNode;
}) {
  const [now, setNow] = useState(() => Date.now());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const waiting = prompt.status === 'Waiting';

  useEffect(() => {
    if (!waiting) return;
    let stop = false;
    const ask = async () => {
      try {
        const next = await api<Prompt>(`/api/pos/mpesa/prompts/${prompt.id}/status`, {
          method: 'POST',
          readOnly: true,
        });
        if (!stop) onChange(next);
      } catch {
        // Offline for a moment: ask again on the next tick.
      }
    };
    const timer = window.setInterval(() => {
      setNow(Date.now());
      void ask();
    }, POLL_MS);
    const off = onPosEvent('mpesa.prompt', (payload) => {
      if ((payload as { promptId?: string }).promptId === prompt.id) void ask();
    });
    return () => {
      stop = true;
      window.clearInterval(timer);
      off();
    };
  }, [waiting, prompt.id, onChange]);

  const cancel = async () => {
    setBusy(true);
    try {
      onChange(await api<Prompt>(`/api/pos/mpesa/prompts/${prompt.id}/cancel`, { method: 'POST' }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  if (waiting)
    return (
      <div className="mpesa-status" role="status">
        <span className="mpesa-step">{t('pos.mpesa.stepSent')}</span>
        <span className="mpesa-ring" aria-hidden="true" />
        <h3 className="mpesa-status__title">{t('pos.mpesa.waiting')}</h3>
        <p>
          {t('pos.mpesa.sentTo', {
            phone: prompt.phone,
            amount: formatMoney(prompt.amount, currency),
          })}
          {' · '}
          <span className="mpesa-mono">{elapsed(prompt.sentAt, now)}</span>
        </p>
        <div className="mpesa-status__actions">
          <Button size="lg" disabled={busy} onClick={onSendAgain}>
            {t('pos.mpesa.sendAgain')}
          </Button>
          <Button size="lg" disabled={busy} onClick={() => void cancel()}>
            {t('common.cancel')}
          </Button>
        </div>
        <p className="mpesa-help">{t('pos.mpesa.expiryNote')}</p>
        {error && <p className="mpesa-error">{error}</p>}
      </div>
    );

  if (prompt.status === 'Paid')
    return (
      <div className="mpesa-status mpesa-status--paid" role="status">
        <span className="mpesa-step">{t('pos.mpesa.stepPaid')}</span>
        <h3 className="mpesa-paid">
          <span className="mpesa-tick" aria-hidden="true">
            ✓
          </span>
          {t('pos.mpesa.received', { amount: formatMoney(prompt.amount, currency) })}
        </h3>
        <dl className="mpesa-facts">
          <dt>{t('pos.mpesa.code')}</dt>
          <dd className="mpesa-mono">{prompt.code ?? t('pos.mpesa.codeComing')}</dd>
          <dt>{t('pos.mpesa.from')}</dt>
          <dd>
            {[prompt.payerName, prompt.payerPhone ?? prompt.phone].filter(Boolean).join(' · ')}
          </dd>
          <dt>{t('pos.mpesa.time')}</dt>
          <dd className="mpesa-mono">{prompt.paidAt ? time(prompt.paidAt) : ''}</dd>
          {!children && prompt.broughtBy && (
            <>
              <dt>{t('pos.settle.broughtBy')}</dt>
              <dd>{prompt.broughtBy}</dd>
            </>
          )}
        </dl>
        {children}
        <p className="mpesa-ok-note">{t('pos.mpesa.addedNote')}</p>
        <Button size="lg" onClick={onDone}>
          {t('common.close')}
        </Button>
      </div>
    );

  return (
    <div className="mpesa-status" role="alert">
      <h3 className="mpesa-status__title">{t(`pos.mpesa.status.${prompt.status}`)}</h3>
      <p>{prompt.message}</p>
      <p className="mpesa-help">
        {t('pos.mpesa.sentTo', { phone: prompt.phone, amount: formatAmount(prompt.amount) })}
      </p>
      <div className="mpesa-status__actions">
        <Button variant="primary" size="lg" onClick={onSendAgain}>
          {t('pos.mpesa.sendAgain')}
        </Button>
        <Button size="lg" onClick={onDone}>
          {t('pos.mpesa.otherWay')}
        </Button>
      </div>
    </div>
  );
}
