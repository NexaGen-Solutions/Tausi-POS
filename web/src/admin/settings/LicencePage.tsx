import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { LicenceStatus } from '../../setup/types';
import '../../setup/setup.css';
import '../stock/stock.css';
import '../reports/reports.css';

function day(iso: string | null) {
  return iso
    ? new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
    : null;
}

/** Settings → Licence: the edition, business, terminals in use of the allowed number, the dates, and Enter new key. Never locked. */
export function LicencePage() {
  const queries = useQueryClient();
  const licence = useQuery({
    queryKey: ['licence'],
    queryFn: () => api<LicenceStatus>('/api/licence'),
  });
  const [key, setKey] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const l = licence.data;
  const enter = async () => {
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      await api('/api/admin/licence', { method: 'POST', body: { key } });
      setKey('');
      setSaved(true);
      await queries.invalidateQueries({ queryKey: ['licence'] });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  return (
    <main className="stock stock--single">
      <div className="stock__main">
        <h1 className="admin__title">{t('admin.licence.title')}</h1>
        {l && (
          <dl className="ticket-facts">
            <dt>{t('admin.licence.state')}</dt>
            <dd>{t(`admin.licence.status.${l.status}`)}</dd>
            <dt>{t('admin.licence.edition')}</dt>
            <dd>{l.edition ?? '-'}</dd>
            <dt>{t('admin.licence.business')}</dt>
            <dd>{l.business ?? '-'}</dd>
            <dt>{t('admin.licence.terminals')}</dt>
            <dd className={l.overLimit ? 'wizard__warn' : undefined}>
              {l.terminalLimit > 0
                ? t('admin.licence.terminalsUsed', {
                    used: l.terminalsUsed,
                    limit: l.terminalLimit,
                  })
                : l.terminalsUsed}
              {l.overLimit && ` · ${t('admin.licence.over')}`}
            </dd>
            <dt>{t('admin.licence.expires')}</dt>
            <dd>
              {day(l.expiresAt) ?? t('admin.licence.never')}
              {l.daysLeft !== null && ` (${t('admin.licence.daysLeft', { days: l.daysLeft })})`}
            </dd>
            <dt>{t('admin.licence.support')}</dt>
            <dd>{day(l.supportUntil) ?? '-'}</dd>
          </dl>
        )}
        <label className="nx-field">
          <span className="nx-field__label">{t('admin.licence.newKey')}</span>
          <textarea
            className="nx-field__input wizard__key"
            rows={4}
            value={key}
            spellCheck={false}
            onChange={(e) => setKey(e.target.value)}
          />
        </label>
        <div className="stock__buttons">
          <Button variant="primary" disabled={busy || !key.trim()} onClick={() => void enter()}>
            {t('admin.licence.enterKey')}
          </Button>
        </div>
        {saved && <p role="status">{t('admin.licence.saved')}</p>}
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
      </div>
    </main>
  );
}
