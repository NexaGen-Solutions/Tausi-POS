import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField } from '../../shared/ui';
import { unitsOf } from './units';
import { wasteReasons, type StockActivity, type StockItemRow, type StockLocation } from './types';

interface Props {
  items: StockItemRow[];
  locations: StockLocation[];
  onDone: (result: StockActivity | null) => void;
}

function useSubmit(onDone: Props['onDone']) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (path: string, body: unknown) => {
    setBusy(true);
    setError(null);
    try {
      onDone(await api<StockActivity>(path, { method: 'POST', body }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  return { error, busy, run };
}

/** Record waste: where, what, how much (any unit of the item) and why. */
export function WasteDialog({ items, locations, onDone }: Props) {
  const active = items.filter((i) => i.active);
  const [location, setLocation] = useState(
    locations.find((l) => l.name === 'Bar')?.id ?? locations[0]?.id ?? '',
  );
  const [itemId, setItemId] = useState(active[0]?.id ?? '');
  const item = active.find((i) => i.id === itemId);
  const [unit, setUnit] = useState(item?.baseUnit ?? '');
  const [quantity, setQuantity] = useState('');
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const { error, busy, run } = useSubmit(onDone);
  const ready =
    !!item && Number(quantity) > 0 && reason !== '' && (reason !== 'Other' || note.trim() !== '');

  return (
    <Modal title={t('admin.stock.recordWaste')} onClose={() => onDone(null)} width={520}>
      <div className="stock-form">
        <SelectField
          label={t('admin.stock.location')}
          value={location}
          onChange={(e) => setLocation(e.target.value)}
          options={locations.map((l) => ({ value: l.id, label: l.name }))}
        />
        <SelectField
          label={t('admin.stock.item')}
          value={itemId}
          onChange={(e) => {
            setItemId(e.target.value);
            setUnit(active.find((i) => i.id === e.target.value)?.baseUnit ?? '');
          }}
          options={active.map((i) => ({ value: i.id, label: i.name }))}
        />
        <div className="stock-form__row">
          <Field
            label={t('admin.stock.quantity')}
            value={quantity}
            inputMode="decimal"
            onChange={(e) => setQuantity(e.target.value.replace(/[^\d.]/g, ''))}
          />
          <SelectField
            label={t('admin.stock.unit')}
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            options={(item ? unitsOf(item) : []).map((u) => ({ value: u, label: u }))}
          />
        </div>
        <div className="stock-reasons" role="group" aria-label={t('admin.stock.reason')}>
          {wasteReasons.map((r) => (
            <button
              key={r}
              type="button"
              className={`stock-choice${reason === r ? ' stock-choice--on' : ''}`}
              aria-pressed={reason === r}
              onClick={() => setReason(r)}
            >
              {t(`admin.stock.wasteReason.${r.replace(' ', '')}`)}
            </button>
          ))}
        </div>
        <Field
          label={reason === 'Other' ? t('admin.stock.noteRequired') : t('admin.stock.noteOptional')}
          value={note}
          maxLength={200}
          onChange={(e) => setNote(e.target.value)}
        />
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
        <div className="stock-dialog__buttons">
          <Button onClick={() => onDone(null)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={!ready || busy}
            onClick={() =>
              void run('/api/admin/stock/waste', {
                locationId: location,
                stockItemId: itemId,
                quantity: Number(quantity),
                unit,
                reason,
                note: note.trim() || null,
              })
            }
          >
            {t('admin.stock.saveWaste')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}

interface TransferLineForm {
  itemId: string;
  quantity: string;
  unit: string;
}

/** Move stock between locations; optionally print a transfer slip for whoever carries it. */
export function TransferDialog({ items, locations, onDone }: Props) {
  const active = items.filter((i) => i.active);
  const printers = useQuery({
    queryKey: ['printers'],
    queryFn: () => api<{ id: string; name: string; active: boolean }[]>('/api/admin/printers'),
    retry: false,
  });
  const [from, setFrom] = useState(
    locations.find((l) => l.name === 'Store')?.id ?? locations[0]?.id ?? '',
  );
  const [to, setTo] = useState(
    locations.find((l) => l.name === 'Bar')?.id ?? locations[1]?.id ?? '',
  );
  const first = active[0];
  const [lines, setLines] = useState<TransferLineForm[]>([
    { itemId: first?.id ?? '', quantity: '', unit: first ? unitsOf(first).at(-1)! : '' },
  ]);
  const [printer, setPrinter] = useState('');
  const { error, busy, run } = useSubmit(onDone);
  const ready =
    from !== to && lines.length > 0 && lines.every((l) => l.itemId && Number(l.quantity) > 0);
  const update = (i: number, change: Partial<TransferLineForm>) =>
    setLines((ls) => ls.map((l, j) => (j === i ? { ...l, ...change } : l)));

  return (
    <Modal title={t('admin.stock.transfer')} onClose={() => onDone(null)} width={640}>
      <div className="stock-form">
        <div className="stock-form__row">
          <SelectField
            label={t('admin.stock.fromLocation')}
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            options={locations.map((l) => ({ value: l.id, label: l.name }))}
          />
          <SelectField
            label={t('admin.stock.toLocation')}
            value={to}
            onChange={(e) => setTo(e.target.value)}
            options={locations.map((l) => ({ value: l.id, label: l.name }))}
          />
        </div>
        {from === to && <p className="nx-field__error">{t('admin.stock.sameLocation')}</p>}
        {lines.map((line, i) => {
          const item = active.find((x) => x.id === line.itemId);
          return (
            <div key={i} className="stock-form__row">
              <SelectField
                label={t('admin.stock.item')}
                value={line.itemId}
                onChange={(e) => {
                  const next = active.find((x) => x.id === e.target.value);
                  update(i, { itemId: e.target.value, unit: next ? unitsOf(next).at(-1)! : '' });
                }}
                options={active.map((x) => ({ value: x.id, label: x.name }))}
              />
              <Field
                label={t('admin.stock.quantity')}
                value={line.quantity}
                inputMode="decimal"
                onChange={(e) => update(i, { quantity: e.target.value.replace(/[^\d.]/g, '') })}
              />
              <SelectField
                label={t('admin.stock.unit')}
                value={line.unit}
                onChange={(e) => update(i, { unit: e.target.value })}
                options={(item ? unitsOf(item) : []).map((u) => ({ value: u, label: u }))}
              />
              <Button
                aria-label={t('admin.stock.removeLine')}
                disabled={lines.length === 1}
                onClick={() => setLines((ls) => ls.filter((_, j) => j !== i))}
              >
                ×
              </Button>
            </div>
          );
        })}
        <Button
          onClick={() =>
            setLines((ls) => [
              ...ls,
              { itemId: first?.id ?? '', quantity: '', unit: first ? unitsOf(first).at(-1)! : '' },
            ])
          }
        >
          {t('admin.stock.addLine')}
        </Button>
        <SelectField
          label={t('admin.stock.printSlip')}
          value={printer}
          onChange={(e) => setPrinter(e.target.value)}
          options={[
            { value: '', label: t('admin.stock.noSlip') },
            ...(printers.data ?? [])
              .filter((p) => p.active)
              .map((p) => ({ value: p.id, label: p.name })),
          ]}
        />
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
        <div className="stock-dialog__buttons">
          <Button onClick={() => onDone(null)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={!ready || busy}
            onClick={() =>
              void run('/api/admin/stock/transfers', {
                fromLocationId: from,
                toLocationId: to,
                lines: lines.map((l) => ({
                  stockItemId: l.itemId,
                  quantity: Number(l.quantity),
                  unit: l.unit,
                })),
                printerId: printer || null,
              })
            }
          >
            {t('admin.stock.saveTransfer')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
