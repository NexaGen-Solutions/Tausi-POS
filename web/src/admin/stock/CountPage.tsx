import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { ApiError, api, apiUrl } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, Field, useToast } from '../../shared/ui';
import type { CountLine, StockCount } from './types';
import './stock.css';

type Entries = Record<string, Record<string, string>>;

const totalOf = (line: CountLine, entered: Record<string, string> | undefined) => {
  const values = Object.entries(entered ?? {}).filter(([, v]) => v.trim() !== '');
  if (values.length === 0) return null;
  return values.reduce(
    (sum, [unit, v]) => sum + Number(v) * (line.units.find((u) => u.name === unit)?.factor ?? 1),
    0,
  );
};

const signed = (n: number) =>
  n > 0 ? `+${formatAmount(n)}` : n < 0 ? `−${formatAmount(-n)}` : '0';

/**
 * Wireframe 13: count one location. Each item takes figures per pack and loose units, totalled in
 * base units. Expected figures stay hidden until it is submitted (a blind count); then the
 * differences show, and someone else allowed approves or rejects it.
 */
export function CountPage() {
  const { id = '' } = useParams();
  const queries = useQueryClient();
  const toast = useToast();
  const count = useQuery({
    queryKey: ['stock', 'count', id],
    queryFn: () => api<StockCount>(`/api/admin/stock-counts/${id}`),
  });
  const [entries, setEntries] = useState<Entries>({});
  const [costs, setCosts] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [rejecting, setRejecting] = useState(false);
  const [note, setNote] = useState('');

  const data = count.data;
  if (!data)
    return (
      <main className="stock stock--single">{count.error ? t('common.somethingWrong') : null}</main>
    );
  const draft = data.status === 'Draft';

  const entered = (line: CountLine): Record<string, string> =>
    entries[line.stockItemId] ??
    Object.fromEntries(Object.entries(line.entered).map(([u, v]) => [u, String(v)]));
  const cost = (line: CountLine) =>
    costs[line.stockItemId] ?? (line.unitCost != null ? String(line.unitCost) : '');
  const uncounted = data.lines.filter((l) => totalOf(l, entered(l)) == null).length;

  const set = (count: StockCount) => queries.setQueryData(['stock', 'count', id], count);
  const run = async (work: () => Promise<StockCount>, done?: string) => {
    setBusy(true);
    setError(null);
    try {
      set(await work());
      setEntries({});
      setCosts({});
      void queries.invalidateQueries({ queryKey: ['stock'] });
      if (done) toast(done);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  const save = () =>
    api<StockCount>(`/api/admin/stock-counts/${id}/lines`, {
      method: 'PUT',
      body: {
        lines: data.lines.map((l) => ({
          stockItemId: l.stockItemId,
          entered: Object.fromEntries(
            Object.entries(entered(l))
              .filter(([, v]) => v.trim() !== '')
              .map(([u, v]) => [u, Number(v)]),
          ),
          unitCost: data.isOpening && cost(l) !== '' ? Number(cost(l)) : null,
        })),
      },
    });

  return (
    <main className="stock stock--single">
      <div className="stock__main">
        <div className="stock__header">
          <div>
            <h1 className="admin__title">
              {data.isOpening
                ? t('admin.counts.openingTitle', { location: data.location })
                : t('admin.counts.countTitle', { location: data.location })}
            </h1>
            <p className="stock-note">
              {t('admin.counts.subtitle', { name: data.countedBy, date: formatDayTime(data.asAt) })}{' '}
              · {t(`admin.counts.status.${data.status}`)}
            </p>
          </div>
          <div className="stock__buttons">
            <Link className="nx-btn nx-btn--link nx-btn--md" to="/admin/stock-counts">
              {t('admin.counts.allCounts')}
            </Link>
            <a
              className="nx-btn nx-btn--secondary nx-btn--md"
              href={apiUrl(`/api/admin/stock-counts/${id}/sheet.pdf`)}
              target="_blank"
              rel="noreferrer"
            >
              {t('admin.counts.printSheet')}
            </a>
            {draft && (
              <>
                <Button
                  disabled={busy}
                  onClick={() => void run(save, t('admin.counts.savedDraft'))}
                >
                  {t('admin.counts.saveDraft')}
                </Button>
                <Button
                  variant="primary"
                  disabled={busy}
                  onClick={() =>
                    void run(async () => {
                      await save();
                      return api<StockCount>(`/api/admin/stock-counts/${id}/submit`, {
                        method: 'POST',
                      });
                    }, t('admin.counts.submitted'))
                  }
                >
                  {t('admin.counts.submit')}
                </Button>
              </>
            )}
            {data.canApprove && !rejecting && (
              <>
                <Button disabled={busy} onClick={() => setRejecting(true)}>
                  {t('admin.stock.reject')}
                </Button>
                <Button
                  variant="primary"
                  disabled={busy}
                  onClick={() =>
                    void run(
                      () =>
                        api<StockCount>(`/api/admin/stock-counts/${id}/approve`, {
                          method: 'POST',
                        }),
                      t('admin.counts.approved'),
                    )
                  }
                >
                  {t('admin.counts.approve')}
                </Button>
              </>
            )}
          </div>
        </div>

        {draft && data.dayOpen && (
          <Banner tone="info">
            {t('admin.counts.dayOpen', { time: formatDayTime(data.asAt) })}
          </Banner>
        )}
        {data.rejectNote && draft && (
          <Banner>{t('admin.counts.rejected', { note: data.rejectNote })}</Banner>
        )}
        {draft && uncounted > 0 && (
          <p className="stock-note">{t('admin.counts.uncounted', { count: uncounted })}</p>
        )}
        {rejecting && (
          <div className="stock-form__row">
            <Field
              label={t('admin.counts.rejectNote')}
              value={note}
              maxLength={300}
              onChange={(e) => setNote(e.target.value)}
              autoFocus
            />
            <Button onClick={() => setRejecting(false)}>{t('common.cancel')}</Button>
            <Button
              variant="danger"
              disabled={busy}
              onClick={() => {
                setRejecting(false);
                void run(
                  () =>
                    api<StockCount>(`/api/admin/stock-counts/${id}/reject`, {
                      method: 'POST',
                      body: { note },
                    }),
                  t('admin.counts.rejectedToast'),
                );
              }}
            >
              {t('admin.counts.confirmReject')}
            </Button>
          </div>
        )}
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}

        <div className="nx-table-box">
          <table className="nx-table">
            <caption className="visually-hidden">
              {t('admin.counts.countTitle', { location: data.location })}
            </caption>
            <thead>
              <tr>
                <th scope="col">{t('admin.stock.item')}</th>
                {!data.blind && (
                  <th scope="col" className="nx-table--right">
                    {t('admin.counts.expected')}
                  </th>
                )}
                <th scope="col" className="nx-table--right">
                  {t('admin.counts.counted')}
                </th>
                {data.isOpening && (
                  <th scope="col" className="nx-table--right">
                    {t('admin.counts.unitCost')}
                  </th>
                )}
                {!data.blind && (
                  <th scope="col" className="nx-table--right">
                    {t('admin.counts.difference')}
                  </th>
                )}
                {!data.blind && (
                  <th scope="col" className="nx-table--right">
                    {t('admin.stock.value')}
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {data.lines.map((line) => {
                const values = entered(line);
                const total = totalOf(line, values);
                const off = line.difference != null && line.difference !== 0;
                return (
                  <tr key={line.stockItemId} className={off ? 'stock-row--low' : undefined}>
                    <th scope="row" className="stock-count-name">
                      {line.stockItem}
                    </th>
                    {!data.blind && (
                      <td className="nx-table--right nx-table--mono">{line.expectedText ?? '—'}</td>
                    )}
                    <td className="nx-table--right">
                      <div className="stock-count-units">
                        {[...line.units]
                          .sort((a, b) => b.factor - a.factor)
                          .map((u) => (
                            <label key={u.name}>
                              <input
                                className="stock-count-input"
                                aria-label={t('admin.counts.entry', {
                                  item: line.stockItem,
                                  unit: u.name,
                                })}
                                inputMode="decimal"
                                disabled={!draft}
                                value={values[u.name] ?? ''}
                                onChange={(e) =>
                                  setEntries((all) => ({
                                    ...all,
                                    [line.stockItemId]: {
                                      ...values,
                                      [u.name]: e.target.value.replace(/[^\d.]/g, ''),
                                    },
                                  }))
                                }
                              />
                              <span className="stock-muted">{u.name}</span>
                            </label>
                          ))}
                        <span className="mono" data-testid={`count-total-${line.stockItem}`}>
                          = {total == null ? '—' : `${formatAmount(total)} ${line.baseUnit}`}
                        </span>
                      </div>
                    </td>
                    {data.isOpening && (
                      <td className="nx-table--right">
                        <input
                          className="stock-count-input"
                          aria-label={t('admin.counts.costOf', {
                            item: line.stockItem,
                            unit: line.baseUnit,
                          })}
                          inputMode="decimal"
                          disabled={!draft}
                          value={cost(line)}
                          onChange={(e) =>
                            setCosts((c) => ({
                              ...c,
                              [line.stockItemId]: e.target.value.replace(/[^\d.]/g, ''),
                            }))
                          }
                        />
                      </td>
                    )}
                    {!data.blind && (
                      <td className={`nx-table--right nx-table--mono${off ? ' stock-warn' : ''}`}>
                        {line.difference == null ? '—' : signed(line.difference)}
                      </td>
                    )}
                    {!data.blind && (
                      <td className={`nx-table--right nx-table--mono${off ? ' stock-warn' : ''}`}>
                        {line.value == null ? '—' : signed(line.value)}
                      </td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        {!data.blind && (
          <>
            <div className="stock-summary">
              <div className="stock-summary__box">
                <span>{t('admin.counts.itemsShort')}</span>
                <span className="mono">{data.itemsShort}</span>
              </div>
              <div
                className={`stock-summary__box${(data.varianceValue ?? 0) !== 0 ? ' stock-summary__box--warn' : ''}`}
                data-testid="total-variance"
              >
                <span>{t('admin.counts.totalVariance')}</span>
                <span className="mono">KSh {signed(data.varianceValue ?? 0)}</span>
              </div>
            </div>
            <p className="stock-note">{t('admin.counts.approveNote')}</p>
          </>
        )}
        {draft && <p className="stock-note">{t('admin.counts.blindNote')}</p>}
      </div>
    </main>
  );
}
