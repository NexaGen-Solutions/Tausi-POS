import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { StockActivity } from './types';

/** Where approving and rejecting each kind of stock document goes. */
function endpoint(item: StockActivity, how: 'approve' | 'reject') {
  if (item.kind === 'Delivery') return `/api/admin/deliveries/${item.id}/${how}`;
  if (item.kind === 'Reversal')
    return how === 'approve'
      ? `/api/admin/deliveries/${item.id}/reverse/approve`
      : `/api/admin/deliveries/${item.id}/reject`;
  return `/api/admin/stock/activity/${item.id}/${how}`;
}

/**
 * "Awaiting approval": stock documents saved as Pending because the creator's key needs approval.
 * Someone Allowed for that key (not the creator) approves; only then does stock move.
 */
export function AwaitingApproval() {
  const queries = useQueryClient();
  const awaiting = useQuery({
    queryKey: ['stock', 'awaiting'],
    queryFn: () => api<StockActivity[]>('/api/admin/stock/awaiting'),
  });
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const decide = async (item: StockActivity, how: 'approve' | 'reject') => {
    setBusy(item.id);
    setError(null);
    try {
      await api(endpoint(item, how), {
        method: 'POST',
        body: how === 'reject' ? { note: null } : undefined,
      });
      await queries.invalidateQueries({ queryKey: ['stock'] });
      await queries.invalidateQueries({ queryKey: ['deliveries'] });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(null);
    }
  };

  if (!awaiting.data?.length) return null;
  return (
    <section className="stock-awaiting" aria-labelledby="stock-awaiting">
      <h2 id="stock-awaiting" className="stock-section-title">
        {t('admin.stock.awaiting', { count: awaiting.data.length })}
      </h2>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <ul className="stock-awaiting__list">
        {awaiting.data.map((item) => (
          <li key={`${item.kind}-${item.id}`} className="stock-awaiting__item">
            <div>
              <strong>{t(`admin.stock.kind.${item.kind}`)}</strong> · {item.summary}
              <span className="stock-muted">
                {' '}
                · {item.location} ·{' '}
                {t('admin.stock.byWhen', { name: item.by, when: formatDayTime(item.at) })}
                {item.value != null && ` · KSh ${formatAmount(item.value)}`}
              </span>
              {item.note && <span className="stock-muted"> · {item.note}</span>}
            </div>
            {item.kind === 'Count' ? (
              <Link
                className="nx-btn nx-btn--secondary nx-btn--md"
                to={`/admin/stock-counts/${item.id}`}
              >
                {t('admin.stock.openCount')}
              </Link>
            ) : (
              <div className="stock-awaiting__buttons">
                <Button disabled={busy === item.id} onClick={() => void decide(item, 'reject')}>
                  {t('admin.stock.reject')}
                </Button>
                <Button
                  variant="primary"
                  disabled={busy === item.id}
                  onClick={() => void decide(item, 'approve')}
                >
                  {t('admin.stock.approve')}
                </Button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
