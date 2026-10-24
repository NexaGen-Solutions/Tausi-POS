import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, SelectField, Table, Toggle, type Column } from '../../shared/ui';
import type { CountListItem, StockCount, StockOverview } from './types';
import './stock.css';

/** Stock counts: date, location, kind, who counted and approved, status and the variance. */
export function CountsPage() {
  const navigate = useNavigate();
  const counts = useQuery({
    queryKey: ['stock', 'counts'],
    queryFn: () => api<CountListItem[]>('/api/admin/stock-counts'),
  });
  const [starting, setStarting] = useState(false);
  const columns: Column<CountListItem>[] = [
    {
      key: 'date',
      header: t('admin.stock.date'),
      render: (c) => (
        <button
          type="button"
          className="stock-link"
          onClick={() => navigate(`/admin/stock-counts/${c.id}`)}
          aria-label={t('admin.counts.open', { location: c.location, date: formatDayTime(c.asAt) })}
        >
          {formatDayTime(c.asAt)}
        </button>
      ),
    },
    { key: 'location', header: t('admin.stock.location'), render: (c) => c.location },
    {
      key: 'kind',
      header: t('admin.stock.kindHeader'),
      render: (c) => (c.isOpening ? t('admin.counts.opening') : t(`admin.counts.kind.${c.kind}`)),
    },
    { key: 'by', header: t('admin.counts.countedBy'), render: (c) => c.countedBy },
    { key: 'approved', header: t('admin.counts.approvedBy'), render: (c) => c.approvedBy ?? '—' },
    {
      key: 'status',
      header: t('admin.stock.statusHeader'),
      render: (c) => t(`admin.counts.status.${c.status}`),
    },
    {
      key: 'variance',
      header: t('admin.counts.variance'),
      align: 'right',
      mono: true,
      render: (c) =>
        c.varianceValue == null ? (
          '—'
        ) : (
          <span className={c.varianceValue < 0 ? 'stock-warn' : undefined}>
            {formatAmount(c.varianceValue)}
          </span>
        ),
    },
  ];
  return (
    <main className="stock stock--single">
      <div className="stock__main">
        <div className="stock__header">
          <h1 className="admin__title">{t('admin.counts.title')}</h1>
          <Button variant="primary" onClick={() => setStarting(true)}>
            {t('admin.counts.start')}
          </Button>
        </div>
        {counts.data && (
          <Table
            caption={t('admin.counts.title')}
            columns={columns}
            rows={counts.data}
            rowKey={(c) => c.id}
            empty={t('admin.counts.none')}
          />
        )}
      </div>
      {starting && (
        <StartCountDialog
          onClose={() => setStarting(false)}
          onStarted={(id) => navigate(`/admin/stock-counts/${id}`)}
        />
      )}
    </main>
  );
}

/** One location; Full (every active item) or Partial (picked items); optionally the opening stock. */
function StartCountDialog({
  onClose,
  onStarted,
}: {
  onClose: () => void;
  onStarted: (id: string) => void;
}) {
  const stock = useQuery({
    queryKey: ['stock', 'overview', ''],
    queryFn: () => api<StockOverview>('/api/admin/stock'),
  });
  const [location, setLocation] = useState('');
  const [kind, setKind] = useState<'Full' | 'Partial'>('Full');
  const [opening, setOpening] = useState(false);
  const [picked, setPicked] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const chosen =
    location ||
    stock.data?.locations.find((l) => l.name === 'Bar')?.id ||
    stock.data?.locations[0]?.id ||
    '';

  const start = async () => {
    setBusy(true);
    setError(null);
    try {
      const count = await api<StockCount>('/api/admin/stock-counts', {
        method: 'POST',
        body: {
          locationId: chosen,
          kind,
          isOpening: opening,
          itemIds: kind === 'Partial' ? picked : null,
        },
      });
      onStarted(count.id);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={t('admin.counts.start')} onClose={onClose} width={520}>
      <div className="stock-form">
        <SelectField
          label={t('admin.stock.location')}
          value={chosen}
          onChange={(e) => setLocation(e.target.value)}
          options={(stock.data?.locations ?? []).map((l) => ({ value: l.id, label: l.name }))}
        />
        <div className="stock-reasons" role="group" aria-label={t('admin.stock.kindHeader')}>
          {(['Full', 'Partial'] as const).map((k) => (
            <button
              key={k}
              type="button"
              className={`stock-choice${kind === k ? ' stock-choice--on' : ''}`}
              aria-pressed={kind === k}
              onClick={() => setKind(k)}
            >
              {t(`admin.counts.kind.${k}`)}
            </button>
          ))}
        </div>
        {kind === 'Partial' && (
          <fieldset className="stock-fieldset">
            <legend>{t('admin.counts.pick')}</legend>
            {(stock.data?.items ?? [])
              .filter((i) => i.active)
              .map((i) => (
                <label key={i.id} className="stock-check">
                  <input
                    type="checkbox"
                    checked={picked.includes(i.id)}
                    onChange={(e) =>
                      setPicked((p) =>
                        e.target.checked ? [...p, i.id] : p.filter((x) => x !== i.id),
                      )
                    }
                  />
                  {i.name}
                </label>
              ))}
          </fieldset>
        )}
        <Toggle label={t('admin.counts.openingToggle')} checked={opening} onChange={setOpening} />
        <p className="stock-note">{t('admin.counts.startHelp')}</p>
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
        <div className="stock-dialog__buttons">
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={busy || !chosen || (kind === 'Partial' && picked.length === 0)}
            onClick={() => void start()}
          >
            {t('admin.counts.startButton')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
