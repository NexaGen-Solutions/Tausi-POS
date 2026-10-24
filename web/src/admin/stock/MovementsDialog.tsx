import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, Table, type Column } from '../../shared/ui';
import type { StockItemRow, StockMovementRow } from './types';

/** An item's movement history: date, kind, quantity, location, who and what caused it, filterable by date. */
export function MovementsDialog({ item, onClose }: { item: StockItemRow; onClose: () => void }) {
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const query = new URLSearchParams();
  if (from) query.set('from', from);
  if (to) query.set('to', to);
  const movements = useQuery({
    queryKey: ['stock', 'movements', item.id, from, to],
    queryFn: () => api<StockMovementRow[]>(`/api/admin/stock/items/${item.id}/movements?${query}`),
  });
  const columns: Column<StockMovementRow>[] = [
    { key: 'at', header: t('admin.stock.date'), render: (m) => formatDayTime(m.at) },
    {
      key: 'kind',
      header: t('admin.stock.kindHeader'),
      render: (m) => t(`admin.stock.movement.${m.kind}`),
    },
    {
      key: 'qty',
      header: t('admin.stock.quantity'),
      align: 'right',
      mono: true,
      render: (m) => (
        <span className={m.quantity < 0 ? 'stock-out' : undefined}>
          {m.quantity > 0 ? '+' : ''}
          {m.quantityText}
        </span>
      ),
    },
    { key: 'location', header: t('admin.stock.location'), render: (m) => m.location },
    { key: 'user', header: t('admin.stock.user'), render: (m) => m.user ?? '—' },
    { key: 'ref', header: t('admin.stock.reference'), render: (m) => m.reference },
  ];
  return (
    <Modal title={t('admin.stock.historyTitle', { name: item.name })} onClose={onClose} width={900}>
      <div className="stock-filters">
        <Field
          label={t('admin.stock.from')}
          type="date"
          value={from}
          onChange={(e) => setFrom(e.target.value)}
        />
        <Field
          label={t('admin.stock.to')}
          type="date"
          value={to}
          onChange={(e) => setTo(e.target.value)}
        />
      </div>
      {movements.data && (
        <Table
          caption={t('admin.stock.historyTitle', { name: item.name })}
          columns={columns}
          rows={movements.data}
          rowKey={(m) => `${m.at}-${m.kind}-${m.quantity}-${m.reference}`}
          empty={t('admin.stock.noMovements')}
        />
      )}
      <div className="stock-dialog__buttons">
        <Button onClick={onClose}>{t('common.close')}</Button>
      </div>
    </Modal>
  );
}
