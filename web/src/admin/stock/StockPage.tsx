import { Link } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import {
  Banner,
  Button,
  Field,
  Modal,
  SelectField,
  Table,
  useToast,
  type Column,
} from '../../shared/ui';
import { AwaitingApproval } from '../approvals/AwaitingApproval';
import { MovementsDialog } from './MovementsDialog';
import { RecipePanel } from './RecipePanel';
import { StockItemDialog } from './StockItemDialog';
import { TransferDialog, WasteDialog } from './WasteTransferDialogs';
import { stockKey, type StockActivity, type StockItemRow, type StockOverview } from './types';
import './stock.css';

type Dialog =
  | { kind: 'waste' }
  | { kind: 'transfer' }
  | { kind: 'item'; item: StockItemRow | null }
  | { kind: 'history'; item: StockItemRow }
  | { kind: 'noRecipe' };

/**
 * Wireframe 11: what is in stock where, its cost and value, with the recipe of a menu item on the
 * right. Orange rows are below their reorder level; negative ones say to check deliveries or the
 * last count. Documents waiting for approval are listed at the top.
 */
export function StockPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const [location, setLocation] = useState('');
  const [search, setSearch] = useState('');
  const [tab, setTab] = useState<'stock' | 'activity'>('stock');
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [recipeFor, setRecipeFor] = useState<string | undefined>();
  const stock = useQuery({
    queryKey: stockKey(location),
    queryFn: () =>
      api<StockOverview>(`/api/admin/stock${location ? `?locationId=${location}` : ''}`),
  });

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (stock.data?.items ?? []).filter((i) => !term || i.name.toLowerCase().includes(term));
  }, [stock.data, search]);

  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['stock'] });
  };
  const done = (saved: StockActivity | null) => {
    setDialog(null);
    if (!saved) return;
    refresh();
    toast(saved.status === 'Pending' ? t('admin.stock.savedPending') : t('admin.stock.saved'));
  };

  const columns: Column<StockItemRow>[] = [
    {
      key: 'name',
      header: t('admin.stock.item'),
      render: (i) => (
        <button
          type="button"
          className="stock-link"
          onClick={() => setDialog({ kind: 'history', item: i })}
          aria-label={t('admin.stock.historyOf', { name: i.name })}
        >
          {i.name}
          {!i.active && <span className="stock-muted"> · {t('admin.stock.inactive')}</span>}
        </button>
      ),
    },
    {
      key: 'unit',
      header: t('admin.stock.unit'),
      render: (i) => (
        <span>
          {i.baseUnit}
          {i.packs.length > 0 && (
            <span className="stock-muted">
              {' '}
              · {i.packs.map((p) => `${p.name} ${p.factor}`).join(', ')}
            </span>
          )}
        </span>
      ),
    },
    {
      key: 'onHand',
      header: t('admin.stock.onHand'),
      align: 'right',
      mono: true,
      render: (i) => (
        <span
          className={i.negative || i.belowReorder ? 'stock-warn' : undefined}
          title={i.negative ? t('admin.stock.belowZero') : undefined}
        >
          {i.onHandText}
        </span>
      ),
    },
    {
      key: 'reorder',
      header: t('admin.stock.reorder'),
      align: 'right',
      mono: true,
      render: (i) => (i.reorderLevel != null ? formatAmount(i.reorderLevel) : '—'),
    },
    {
      key: 'cost',
      header: t('admin.stock.cost'),
      align: 'right',
      mono: true,
      render: (i) => formatAmount(Math.round(i.averageCost * 100) / 100),
    },
    {
      key: 'value',
      header: t('admin.stock.value'),
      align: 'right',
      mono: true,
      render: (i) => formatAmount(i.value),
    },
  ];
  if (stock.data?.canEdit)
    columns.push({
      key: 'edit',
      header: '',
      render: (i) => (
        <Button
          aria-label={t('admin.stock.editItem', { name: i.name })}
          onClick={() => setDialog({ kind: 'item', item: i })}
        >
          {t('common.edit')}
        </Button>
      ),
    });

  const data = stock.data;
  const negative = rows.filter((r) => r.negative);
  return (
    <main className="stock">
      <div className="stock__main">
        <div className="stock__header">
          <h1 className="admin__title">{t('admin.stock.title')}</h1>
          <div className="stock__buttons">
            <Link className="nx-btn nx-btn--link nx-btn--md" to="/admin/stock/locations">
              {t('admin.locations.title')}
            </Link>
            <Button onClick={() => setDialog({ kind: 'waste' })} disabled={!data}>
              {t('admin.stock.recordWaste')}
            </Button>
            <Button onClick={() => setDialog({ kind: 'transfer' })} disabled={!data}>
              {t('admin.stock.transfer')}
            </Button>
            {data?.canEdit && (
              <Button variant="primary" onClick={() => setDialog({ kind: 'item', item: null })}>
                {t('admin.stock.addItem')}
              </Button>
            )}
          </div>
        </div>

        <AwaitingApproval only="stock" />

        {data && data.noRecipe.length > 0 && (
          <Banner
            action={{
              label: t('admin.stock.showList'),
              onClick: () => setDialog({ kind: 'noRecipe' }),
            }}
          >
            {t('admin.stock.noRecipeWarning', { count: data.noRecipe.length })}
          </Banner>
        )}
        {negative.length > 0 && (
          <Banner>
            {t('admin.stock.negativeWarning', { names: negative.map((n) => n.name).join(', ') })}
          </Banner>
        )}

        <div className="stock__tabs" role="tablist" aria-label={t('admin.stock.title')}>
          {(['stock', 'activity'] as const).map((k) => (
            <button
              key={k}
              type="button"
              role="tab"
              aria-selected={tab === k}
              className={`stock-tab${tab === k ? ' stock-tab--on' : ''}`}
              onClick={() => setTab(k)}
            >
              {t(`admin.stock.tab.${k}`)}
            </button>
          ))}
        </div>

        {tab === 'stock' ? (
          <>
            <div className="stock-filters">
              <SelectField
                label={t('admin.stock.location')}
                value={location}
                onChange={(e) => setLocation(e.target.value)}
                options={[
                  { value: '', label: t('admin.stock.allLocations') },
                  ...(data?.locations ?? []).map((l) => ({ value: l.id, label: l.name })),
                ]}
              />
              <Field
                label={t('admin.stock.search')}
                type="search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
              {data && (
                <p className="stock-total">
                  {t('admin.stock.totalValue')}{' '}
                  <span className="mono">KSh {formatAmount(data.totalValue)}</span>
                </p>
              )}
            </div>
            {data && (
              <Table
                caption={t('admin.stock.title')}
                columns={columns}
                rows={rows}
                rowKey={(i) => i.id}
                rowClassName={(i) => (i.negative || i.belowReorder ? 'stock-row--low' : undefined)}
                empty={t('admin.stock.noItems')}
              />
            )}
            <p className="stock-note">{t('admin.stock.legend')}</p>
          </>
        ) : (
          <ActivityTab />
        )}
      </div>

      {data && (
        <RecipePanel
          key={recipeFor ?? 'first'}
          items={data.items}
          canEdit={data.canEdit}
          initialMenuItem={recipeFor}
        />
      )}

      {dialog?.kind === 'waste' && data && (
        <WasteDialog items={data.items} locations={data.locations} onDone={done} />
      )}
      {dialog?.kind === 'transfer' && data && (
        <TransferDialog items={data.items} locations={data.locations} onDone={done} />
      )}
      {dialog?.kind === 'item' && data && (
        <StockItemDialog
          item={dialog.item}
          locations={data.locations}
          onDone={(saved) => {
            setDialog(null);
            if (saved) refresh();
          }}
        />
      )}
      {dialog?.kind === 'history' && (
        <MovementsDialog item={dialog.item} onClose={() => setDialog(null)} />
      )}
      {dialog?.kind === 'noRecipe' && data && (
        <Modal title={t('admin.stock.noRecipeTitle')} onClose={() => setDialog(null)} width={480}>
          <p>{t('admin.stock.noRecipeHelp')}</p>
          <ul className="stock-plain-list">
            {data.noRecipe.map((n) => (
              <li key={n.menuItemId}>
                <button
                  type="button"
                  className="stock-link"
                  onClick={() => {
                    setRecipeFor(n.menuItemId);
                    setDialog(null);
                  }}
                >
                  {n.name}
                </button>
              </li>
            ))}
          </ul>
          <div className="stock-dialog__buttons">
            <Button onClick={() => setDialog(null)}>{t('common.close')}</Button>
          </div>
        </Modal>
      )}
    </main>
  );
}

/** Waste and transfers, newest first, filterable by kind and date. */
function ActivityTab() {
  const [kind, setKind] = useState('');
  const [from, setFrom] = useState('');
  const query = new URLSearchParams();
  if (kind) query.set('kind', kind);
  if (from) query.set('from', from);
  const activity = useQuery({
    queryKey: ['stock', 'activity', kind, from],
    queryFn: () => api<StockActivity[]>(`/api/admin/stock/activity?${query}`),
  });
  const columns: Column<StockActivity>[] = [
    { key: 'at', header: t('admin.stock.date'), render: (a) => formatDayTime(a.at) },
    {
      key: 'kind',
      header: t('admin.stock.kindHeader'),
      render: (a) => t(`admin.stock.kind.${a.kind}`),
    },
    { key: 'summary', header: t('admin.stock.what'), render: (a) => a.summary },
    { key: 'location', header: t('admin.stock.location'), render: (a) => a.location },
    {
      key: 'value',
      header: t('admin.stock.value'),
      align: 'right',
      mono: true,
      render: (a) => (a.value != null ? formatAmount(a.value) : '—'),
    },
    {
      key: 'status',
      header: t('admin.stock.statusHeader'),
      render: (a) => t(`admin.stock.status.${a.status}`),
    },
    {
      key: 'by',
      header: t('admin.stock.user'),
      render: (a) =>
        a.approvedBy ? `${a.by} · ${t('admin.stock.approvedBy', { name: a.approvedBy })}` : a.by,
    },
  ];
  return (
    <>
      <div className="stock-filters">
        <SelectField
          label={t('admin.stock.kindHeader')}
          value={kind}
          onChange={(e) => setKind(e.target.value)}
          options={[
            { value: '', label: t('admin.stock.allKinds') },
            { value: 'Waste', label: t('admin.stock.kind.Waste') },
            { value: 'Transfer', label: t('admin.stock.kind.Transfer') },
          ]}
        />
        <Field
          label={t('admin.stock.from')}
          type="date"
          value={from}
          onChange={(e) => setFrom(e.target.value)}
        />
      </div>
      {activity.data && (
        <Table
          caption={t('admin.stock.tab.activity')}
          columns={columns}
          rows={activity.data}
          rowKey={(a) => a.id}
          empty={t('admin.stock.noActivity')}
        />
      )}
    </>
  );
}
