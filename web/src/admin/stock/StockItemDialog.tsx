import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField, Toggle } from '../../shared/ui';
import {
  stockCategories,
  stockUnits,
  type StockCategory,
  type StockItemRow,
  type StockLocation,
  type StockUnit,
} from './types';

const unitOf = (baseUnit: string): StockUnit =>
  (stockUnits.find((u) => u.toLowerCase() === baseUnit.toLowerCase()) ?? 'Bottle') as StockUnit;

/** Add or edit a stock item: name, category, base unit, packs, reorder levels per location, active. */
export function StockItemDialog({
  item,
  locations,
  onDone,
}: {
  item: StockItemRow | null;
  locations: StockLocation[];
  onDone: (saved: boolean) => void;
}) {
  const [name, setName] = useState(item?.name ?? '');
  const [category, setCategory] = useState<StockCategory>(item?.category ?? 'Beer');
  const [unit, setUnit] = useState<StockUnit>(item ? unitOf(item.baseUnit) : 'Bottle');
  const [packs, setPacks] = useState(
    (item?.packs ?? []).map((p) => ({ name: p.name, factor: String(p.factor) })),
  );
  const [reorder, setReorder] = useState<Record<string, string>>(
    Object.fromEntries(
      locations.map((l) => [
        l.id,
        String(item?.levels.find((x) => x.locationId === l.id)?.reorderLevel ?? ''),
      ]),
    ),
  );
  const [active, setActive] = useState(item?.active ?? true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const unitLocked = !!item?.hasMovements;

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api(item ? `/api/admin/stock/items/${item.id}` : '/api/admin/stock/items', {
        method: item ? 'PUT' : 'POST',
        body: {
          name,
          category,
          baseUnit: unit,
          packs: packs
            .filter((p) => p.name.trim())
            .map((p) => ({ name: p.name, factor: Number(p.factor) })),
          active,
          reorderLevels: Object.fromEntries(
            Object.entries(reorder).map(([id, v]) => [id, v.trim() === '' ? null : Number(v)]),
          ),
          rowVersion: item?.rowVersion ?? null,
        },
      });
      onDone(true);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      title={item ? t('admin.stock.editItem', { name: item.name }) : t('admin.stock.addItem')}
      onClose={() => onDone(false)}
      width={560}
    >
      <div className="stock-form">
        <Field
          label={t('admin.stock.itemName')}
          value={name}
          maxLength={80}
          onChange={(e) => setName(e.target.value)}
          autoFocus
        />
        <div className="stock-form__row">
          <SelectField
            label={t('admin.stock.category')}
            value={category}
            onChange={(e) => setCategory(e.target.value as StockCategory)}
            options={stockCategories.map((c) => ({
              value: c,
              label: t(`admin.stock.categories.${c}`),
            }))}
          />
          <SelectField
            label={t('admin.stock.baseUnit')}
            value={unit}
            disabled={unitLocked}
            onChange={(e) => setUnit(e.target.value as StockUnit)}
            options={stockUnits.map((u) => ({ value: u, label: t(`admin.stock.units.${u}`) }))}
          />
        </div>
        {unitLocked && <p className="nx-field__hint">{t('admin.stock.unitLocked')}</p>}

        <fieldset className="stock-fieldset">
          <legend>{t('admin.stock.packs')}</legend>
          <p className="nx-field__hint">
            {t('admin.stock.packsHelp', { unit: t(`admin.stock.units.${unit}`) })}
          </p>
          {packs.map((pack, i) => (
            <div key={i} className="stock-form__row">
              <Field
                label={t('admin.stock.packName')}
                value={pack.name}
                maxLength={30}
                onChange={(e) =>
                  setPacks((ps) => ps.map((p, j) => (j === i ? { ...p, name: e.target.value } : p)))
                }
              />
              <Field
                label={t('admin.stock.packFactor', { unit: t(`admin.stock.units.${unit}`) })}
                value={pack.factor}
                inputMode="decimal"
                onChange={(e) =>
                  setPacks((ps) =>
                    ps.map((p, j) => (j === i ? { ...p, factor: e.target.value } : p)),
                  )
                }
              />
              <Button
                aria-label={t('admin.stock.removePack', { name: pack.name || '' })}
                onClick={() => setPacks((ps) => ps.filter((_, j) => j !== i))}
              >
                ×
              </Button>
            </div>
          ))}
          <Button onClick={() => setPacks((ps) => [...ps, { name: '', factor: '' }])}>
            {t('admin.stock.addPack')}
          </Button>
        </fieldset>

        <fieldset className="stock-fieldset">
          <legend>{t('admin.stock.reorderLevels')}</legend>
          <div className="stock-form__row">
            {locations.map((l) => (
              <Field
                key={l.id}
                label={l.name}
                value={reorder[l.id] ?? ''}
                inputMode="decimal"
                hint={t('admin.stock.inUnit', { unit: t(`admin.stock.units.${unit}`) })}
                onChange={(e) => setReorder((r) => ({ ...r, [l.id]: e.target.value }))}
              />
            ))}
          </div>
        </fieldset>
        <Toggle label={t('admin.stock.active')} checked={active} onChange={setActive} />
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
        <div className="stock-dialog__buttons">
          <Button onClick={() => onDone(false)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={busy || name.trim().length < 2}
            onClick={() => void save()}
          >
            {t('common.save')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
