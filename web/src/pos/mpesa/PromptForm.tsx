import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatAmount, formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Keypad, Modal } from '../../shared/ui';
import { spacedPhone, validPhone, type Prompt } from './types';
import './mpesa.css';

/**
 * Boards 22 and 25: the customer's phone number and the amount (the remaining amount, which can be
 * lowered for a split payment), typed on the keypad, then Send prompt. A prompt already waiting on
 * the ticket asks "Cancel it and send a new one?".
 */
export function PromptForm({
  ticketId,
  remaining,
  currency,
  business,
  fromOrderScreen,
  disabled,
  onSent,
  onCancel,
  initialPhone = '',
}: {
  ticketId: string;
  remaining: number;
  currency: string;
  business: string;
  fromOrderScreen: boolean;
  disabled?: boolean;
  onSent: (prompt: Prompt, phone: string) => void;
  onCancel?: () => void;
  initialPhone?: string;
}) {
  const whole = Math.floor(remaining);
  const [phone, setPhone] = useState(initialPhone);
  const [amount, setAmount] = useState(String(whole));
  const [field, setField] = useState<'phone' | 'amount'>('phone');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [replace, setReplace] = useState(false);
  const value = Number(amount || 0);
  const tooMuch = value > remaining;
  const ready = validPhone(phone) && value >= 1 && !tooMuch && !disabled && !busy;

  const send = async (replaceWaiting: boolean) => {
    setBusy(true);
    setError(null);
    try {
      const prompt = await api<Prompt>(`/api/pos/mpesa/tickets/${ticketId}/prompts`, {
        method: 'POST',
        body: { phone, amount: value, replace: replaceWaiting, fromOrderScreen },
      });
      setReplace(false);
      onSent(prompt, phone);
    } catch (e) {
      if (e instanceof ApiError && e.problem.code === 'mpesa_prompt_waiting') setReplace(true);
      else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const type = (digit: string) => {
    if (field === 'phone') setPhone((p) => (p.replace(/\D/g, '').length < 12 ? p + digit : p));
    else setAmount((a) => (a.length < 7 ? (a === '0' ? digit : a + digit) : a));
  };
  const clear = () => (field === 'phone' ? setPhone('') : setAmount(''));
  const back = () =>
    field === 'phone' ? setPhone((p) => p.slice(0, -1)) : setAmount((a) => a.slice(0, -1));

  return (
    <div className="mpesa-prompt">
      <div className="mpesa-prompt__form">
        <label className="mpesa-field">
          <span>{t('pos.mpesa.phone')}</span>
          <input
            className={`mpesa-input${field === 'phone' ? ' mpesa-input--on' : ''}`}
            inputMode="tel"
            value={spacedPhone(phone)}
            onFocus={() => setField('phone')}
            onChange={(e) => setPhone(e.target.value.replace(/[^\d+]/g, ''))}
            disabled={disabled}
            autoComplete="off"
          />
        </label>
        <label className="mpesa-field">
          <span>{t('pos.mpesa.amount')}</span>
          <input
            className={`mpesa-input${field === 'amount' ? ' mpesa-input--on' : ''}`}
            inputMode="numeric"
            value={amount === '' ? '' : formatAmount(Number(amount))}
            onFocus={() => setField('amount')}
            onChange={(e) => setAmount(e.target.value.replace(/\D/g, ''))}
            aria-invalid={tooMuch || undefined}
            disabled={disabled}
          />
        </label>
        {tooMuch && (
          <p className="mpesa-error" role="alert">
            {t('pos.mpesa.tooMuch', { amount: formatAmount(remaining) })}
          </p>
        )}
        <p className="mpesa-help">
          {fromOrderScreen
            ? t('pos.mpesa.orderHelp')
            : t('pos.mpesa.settleHelp', { amount: formatMoney(value, currency), business })}
        </p>
        {error && (
          <p className="mpesa-error" role="alert">
            {error}
          </p>
        )}
        <div className="mpesa-prompt__actions">
          {onCancel && (
            <Button size="lg" onClick={onCancel}>
              {t('common.cancel')}
            </Button>
          )}
          <Button
            variant="primary"
            size="lg"
            block={!onCancel}
            disabled={!ready}
            onClick={() => void send(false)}
          >
            {t('pos.mpesa.send', { amount: formatMoney(value, currency) })}
          </Button>
        </div>
      </div>
      <div className="mpesa-prompt__pad">
        <Keypad
          variant="compact"
          onDigit={type}
          onClear={clear}
          onEnter={back}
          enterLabel={t('pos.actions.backspace')}
          disabled={disabled}
        />
      </div>
      {replace && (
        <Modal title={t('pos.mpesa.replaceTitle')} onClose={() => setReplace(false)} width={480}>
          <p>{t('pos.mpesa.replaceText')}</p>
          <div className="mpesa-prompt__actions">
            <Button size="lg" onClick={() => setReplace(false)}>
              {t('pos.mpesa.keepWaiting')}
            </Button>
            <Button variant="primary" size="lg" disabled={busy} onClick={() => void send(true)}>
              {t('pos.mpesa.sendNew')}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
