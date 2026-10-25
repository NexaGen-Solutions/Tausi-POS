import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { SummaryLine } from '../types';

/** Finish: what was set up, what was skipped (with where to do it later), and Finish setup. */
export function FinishStep({ onBack }: { onBack: () => void }) {
  const summary = useQuery({
    queryKey: ['setup', 'summary'],
    queryFn: () => api<{ lines: SummaryLine[] }>('/api/setup/summary'),
  });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const finish = async () => {
    setBusy(true);
    setError(null);
    try {
      await api('/api/setup/finish', { method: 'POST' });
      window.location.assign('/admin');
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
      setBusy(false);
    }
  };
  return (
    <>
      <p className="wizard__count">{t('wizard.finish.count')}</p>
      <h2 className="wizard__title">{t('wizard.finish.title')}</h2>
      <ul className="wizard__summary">
        {(summary.data?.lines ?? []).map((l) => (
          <li key={l.step} className={l.skipped ? 'wizard__summary--skipped' : undefined}>
            <strong>{l.title}</strong>
            <span>{l.text}</span>
            {l.laterLink && (
              <a href={l.laterLink} target="_blank" rel="noreferrer">
                {t('wizard.finish.doLater')}
              </a>
            )}
          </li>
        ))}
      </ul>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <div className="wizard__footer">
        <Button onClick={onBack}>{t('common.back')}</Button>
      </div>
      <div className="wizard__next">
        <Button variant="primary" size="lg" disabled={busy} onClick={() => void finish()}>
          {t('wizard.finish.button')}
        </Button>
      </div>
    </>
  );
}
