import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../shared/api/client';
import { OfflineBanner } from '../shared/connection/OfflineBanner';
import { t } from '../shared/i18n/i18n';
import { pinSession, setPinSession } from '../shared/session/session';
import { PinPad } from '../shared/ui';
import { PosTopBar } from './PosTopBar';
import { usePinSession } from './usePinSession';

const messageFor = (code?: string) => {
  const key = `pos.changePin.${code}`;
  const text = t(key);
  return text === key ? t('common.somethingWrong') : text;
};

/** First login (or after an admin reset): choose a new PIN, then type it again. */
export function ChangePinPage() {
  const navigate = useNavigate();
  const session = usePinSession();
  const [step, setStep] = useState<'choose' | 'confirm'>('choose');
  const [first, setFirst] = useState('');
  const [pin, setPin] = useState('');
  const [message, setMessage] = useState<string | null>(null);
  const [shake, setShake] = useState(0);
  const [busy, setBusy] = useState(false);

  const startOver = (text: string) => {
    setStep('choose');
    setFirst('');
    setPin('');
    setMessage(text);
    setShake((n) => n + 1);
  };

  const submit = async (value: string) => {
    if (step === 'choose') {
      setFirst(value);
      setPin('');
      setMessage(null);
      setStep('confirm');
      return;
    }
    if (value !== first) {
      startOver(t('pos.changePin.mismatch'));
      return;
    }
    setBusy(true);
    try {
      await api('/api/auth/pin/change', { method: 'POST', body: { newPin: value } });
      const current = pinSession();
      if (current) setPinSession({ ...current, mustChangePin: false });
      navigate('/pos/day', { replace: true });
    } catch (error) {
      startOver(
        error instanceof ApiError ? messageFor(error.problem.code) : t('common.somethingWrong'),
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="pos-screen">
      <PosTopBar />
      <OfflineBanner />
      <main className="pos-center">
        <h1 className="pos-center__title">
          {step === 'choose' ? t('pos.changePin.choose') : t('pos.changePin.confirm')}
        </h1>
        <p className="pos-center__text">
          {step === 'choose'
            ? t('pos.changePin.help', { name: session?.user.name ?? '' })
            : t('pos.changePin.confirmHelp')}
        </p>
        <PinPad
          key={step}
          value={pin}
          onChange={setPin}
          onSubmit={(value) => void submit(value)}
          disabled={busy}
          shakeKey={shake}
        />
        <p className="pin-login__message" role="alert">
          {message}
        </p>
      </main>
    </div>
  );
}
