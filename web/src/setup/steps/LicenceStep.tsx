import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import { NextButton, type StepProps } from '../SetupWizard';
import type { LicenceStatus } from '../types';

/** Step 6, Licence key: a key from NexaGen, checked offline against the business name, or the 30-day trial. */
export function LicenceStep({ status, onNext }: StepProps) {
  const done = status.steps.find((s) => s.key === 'licence')?.status === 'Done';
  const [key, setKey] = useState('');
  const [licence, setLicence] = useState<LicenceStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const send = async (path: string, body?: unknown) => {
    setBusy(true);
    setError(null);
    try {
      setLicence(await api<LicenceStatus>(path, { method: 'POST', body }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="wizard__form">
      <label className="nx-field">
        <span className="nx-field__label">{t('wizard.licence.key')}</span>
        <textarea
          className="nx-field__input wizard__key"
          rows={5}
          value={key}
          spellCheck={false}
          onChange={(e) => setKey(e.target.value)}
          placeholder="TAUSI-XXXXX-XXXXX-…"
        />
      </label>
      <div className="wizard__row">
        <Button
          variant="primary"
          disabled={busy || !key.trim()}
          onClick={() => void send('/api/setup/licence', { key })}
        >
          {t('wizard.licence.enter')}
        </Button>
        <Button disabled={busy} onClick={() => void send('/api/setup/licence/trial')}>
          {t('wizard.licence.trial')}
        </Button>
      </div>
      <p className="wizard__note">{t('wizard.licence.help')}</p>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      {licence && (
        <p className="wizard__ok" role="status">
          {licence.isTrial
            ? t('wizard.licence.trialStarted', {
                days: licence.daysLeft ?? 30,
                terminals: licence.terminalLimit,
              })
            : t('wizard.licence.accepted', {
                edition: licence.edition ?? '',
                business: licence.business ?? '',
                terminals: licence.terminalLimit,
              })}
        </p>
      )}
      <NextButton next="finish" disabled={!licence && !done} onClick={onNext} />
    </div>
  );
}
