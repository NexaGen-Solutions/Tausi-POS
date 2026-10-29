import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { formatMoneyCents } from '../../shared/format/money';
import { vatIncluded } from '../../shared/format/vat';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField } from '../../shared/ui';
import { ItemPicture } from './ItemPicture';
import {
  parsePrice,
  type AdminMenu,
  type MenuItem,
  type PriceLogEntry,
  type SavedRef,
} from './types';

export interface ItemPanelProps {
  menu: AdminMenu;
  /** Editing this item; adding when absent. */
  item?: MenuItem;
  onSaved: () => void;
  onStale: () => void;
  onCancel: () => void;
  /** The picture was changed (saved at once): refresh the list, keep the panel open. */
  onPicture?: () => void;
}

type Errors = Partial<Record<string, string>>;

/**
 * Add or edit an item. Details and prices are saved with separate calls (details need menu.edit,
 * prices menu.edit_prices), behind one Save button.
 */
export function ItemPanel({ menu, item, onSaved, onStale, onCancel, onPicture }: ItemPanelProps) {
  const lists = menu.priceLists.filter((l) => l.active || item?.prices[l.id] !== undefined);
  const defaultList = menu.priceLists.find((l) => l.isDefault)!;
  const activeCategories = menu.categories.filter((c) => c.active || c.id === item?.categoryId);

  const [name, setName] = useState(item?.name ?? '');
  // A picture change saves at once and moves the item's row version on.
  const [rowVersion, setRowVersion] = useState(item?.rowVersion ?? 0);
  const [picture, setPicture] = useState(item?.imageFileId ?? null);
  const [shortName, setShortName] = useState(item?.shortName ?? '');
  const [categoryId, setCategoryId] = useState(item?.categoryId ?? activeCategories[0]?.id ?? '');
  const [vatMode, setVatMode] = useState<'default' | 'zero' | 'exempt' | 'custom'>(
    item?.vatExempt
      ? 'exempt'
      : item?.vatRate == null
        ? 'default'
        : item.vatRate === 0
          ? 'zero'
          : 'custom',
  );
  const [customVat, setCustomVat] = useState(
    item?.vatRate && item.vatRate > 0 ? String(item.vatRate) : '',
  );
  const [active, setActive] = useState(item?.active ?? true);
  const [erpCode, setErpCode] = useState(item?.erpItemCode ?? '');
  const [groupIds, setGroupIds] = useState<string[]>(item?.ownGroupIds ?? []);
  const [prices, setPrices] = useState<Record<string, string>>(() =>
    Object.fromEntries(
      lists.map((l) => [l.id, item?.prices[l.id] !== undefined ? String(item.prices[l.id]) : '']),
    ),
  );
  const [errors, setErrors] = useState<Errors>({});
  const [busy, setBusy] = useState(false);
  const [confirmZero, setConfirmZero] = useState(false);

  const history = useQuery({
    queryKey: ['price-log', item?.id],
    queryFn: () => api<PriceLogEntry[]>(`/api/admin/menu/price-log?itemId=${item!.id}&take=10`),
    enabled: !!item,
    // Always fresh: it is opened right after prices are saved.
    staleTime: 0,
  });

  const shownShort = shortName || name.trim().slice(0, 18);
  const vatRate =
    vatMode === 'default'
      ? menu.businessVatRate
      : vatMode === 'exempt' || vatMode === 'zero'
        ? 0
        : Number(customVat);
  const category = menu.categories.find((c) => c.id === categoryId);
  const inherited = menu.groups.filter((g) =>
    item ? item.inheritedGroupIds.includes(g.id) : g.categoryIds.includes(categoryId),
  );

  const validate = (): Errors => {
    const found: Errors = {};
    if (name.trim().length < 2 || name.trim().length > 60) found.name = t('admin.menu.errors.name');
    if (shortName.length > 18) found.shortName = t('admin.menu.errors.shortName');
    if (!categoryId) found.category = t('admin.menu.errors.category');
    if (vatMode === 'custom' && !(Number(customVat) > 0 && Number(customVat) < 100))
      found.vat = t('admin.menu.errors.vat');
    if (menu.canEditPrices) {
      for (const list of lists) {
        const value = parsePrice(prices[list.id]);
        if (value !== null && (Number.isNaN(value) || value < 0))
          found[list.id] = t('admin.menu.errors.price');
      }
      if (parsePrice(prices[defaultList.id]) === null)
        found[defaultList.id] = t('admin.menu.errors.defaultPrice', { list: defaultList.name });
    }
    return found;
  };

  const changedPrices = () => {
    const changed: Record<string, number | null> = {};
    for (const list of lists) {
      const value = parsePrice(prices[list.id]);
      const before = item?.prices[list.id] ?? null;
      if (value !== before) changed[list.id] = value;
    }
    return changed;
  };

  const save = async (allowZero = false) => {
    const found = validate();
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    const priceChanges = menu.canEditPrices ? changedPrices() : {};
    if (!allowZero && Object.values(priceChanges).some((p) => p === 0)) {
      setConfirmZero(true);
      return;
    }
    setConfirmZero(false);
    setBusy(true);
    try {
      let ref: SavedRef | undefined = item ? { id: item.id, rowVersion } : undefined;
      if (menu.canEdit) {
        const body = {
          name: name.trim(),
          shortName: shortName.trim() || null,
          categoryId,
          vatRate: vatMode === 'default' ? null : vatRate,
          vatExempt: vatMode === 'exempt',
          active,
          erpItemCode: erpCode.trim() || null,
          groupIds,
          rowVersion,
        };
        ref = await api<SavedRef>(
          item ? `/api/admin/menu/items/${item.id}` : '/api/admin/menu/items',
          {
            method: item ? 'PUT' : 'POST',
            body,
          },
        );
      }
      if (ref && Object.keys(priceChanges).length > 0) {
        await api(`/api/admin/menu/items/${ref.id}/prices`, {
          method: 'PUT',
          body: { prices: priceChanges, allowZero, rowVersion: ref.rowVersion },
        });
      }
      onSaved();
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setErrors({ form: t('admin.menu.stale') });
        onStale();
      } else {
        setErrors({ form: error instanceof ApiError ? error.message : t('common.somethingWrong') });
      }
    } finally {
      setBusy(false);
    }
  };

  const readOnly = !menu.canEdit;

  return (
    <form
      className="menu-panel__form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <Field
        label={t('admin.menu.name')}
        value={name}
        maxLength={60}
        onChange={(e) => setName(e.target.value)}
        error={errors.name}
        disabled={readOnly}
      />
      <Field
        label={t('admin.menu.shortName')}
        value={shortName}
        placeholder={name.trim().slice(0, 18)}
        onChange={(e) => setShortName(e.target.value)}
        hint={`${t('admin.menu.shortNameCount', { count: shownShort.length })} · ${t('admin.menu.shortNameHint')}`}
        error={errors.shortName}
        disabled={readOnly}
      />
      {item && (
        <ItemPicture
          itemId={item.id}
          imageFileId={picture}
          disabled={readOnly}
          onChanged={(ref, imageFileId) => {
            setRowVersion(ref.rowVersion);
            setPicture(imageFileId);
            onPicture?.();
          }}
        />
      )}
      <SelectField
        label={t('admin.menu.category')}
        value={categoryId}
        onChange={(e) => setCategoryId(e.target.value)}
        options={activeCategories.map((c) => ({ value: c.id, label: c.name }))}
        error={errors.category}
        disabled={readOnly}
      />
      <SelectField
        label={t('admin.menu.vatRate')}
        value={vatMode}
        onChange={(e) => setVatMode(e.target.value as typeof vatMode)}
        options={[
          { value: 'default', label: t('admin.menu.vatDefault', { rate: menu.businessVatRate }) },
          { value: 'zero', label: t('admin.menu.vatZero') },
          { value: 'exempt', label: t('admin.menu.vatExempt') },
          { value: 'custom', label: t('admin.menu.vatCustom') },
        ]}
        disabled={readOnly}
      />
      {vatMode === 'custom' && (
        <Field
          label={t('admin.menu.customRate')}
          inputMode="decimal"
          value={customVat}
          onChange={(e) => setCustomVat(e.target.value)}
          error={errors.vat}
          disabled={readOnly}
        />
      )}

      <fieldset className="menu-panel__prices">
        {!menu.canEditPrices && (
          <p className="menu-panel__note">{t('admin.menu.pricesReadOnly')}</p>
        )}
        {lists.map((list) => {
          const value = parsePrice(prices[list.id]);
          const amounts =
            value !== null && !Number.isNaN(value) && value >= 0 && Number.isFinite(vatRate)
              ? vatIncluded(value, vatRate)
              : null;
          return (
            <div key={list.id} className="menu-panel__price">
              <Field
                label={t('admin.menu.price', { list: list.name })}
                inputMode="decimal"
                className="menu-panel__money"
                value={prices[list.id]}
                onChange={(e) => setPrices((p) => ({ ...p, [list.id]: e.target.value }))}
                readOnly={!menu.canEditPrices}
                aria-readonly={!menu.canEditPrices}
                error={errors[list.id]}
              />
              {amounts && (
                <p className="menu-panel__vat" aria-live="polite">
                  {t('admin.menu.netVat', {
                    net: formatMoneyCents(amounts.net, menu.currency),
                    vat: formatMoneyCents(amounts.vat, menu.currency),
                  })}
                </p>
              )}
            </div>
          );
        })}
        {!menu.canEditPrices && item && item.prices[defaultList.id] === undefined && (
          <p className="menu-panel__note">{t('admin.menu.notSold', { list: defaultList.name })}</p>
        )}
      </fieldset>

      <fieldset className="menu-panel__groups">
        <legend>{t('admin.menu.groupsOwn')}</legend>
        {menu.groups.length === 0 && (
          <p className="menu-panel__note">{t('admin.menu.groupsNone')}</p>
        )}
        {menu.groups
          .filter((g) => g.active || groupIds.includes(g.id))
          .map((g) => (
            <label key={g.id} className="menu-panel__check">
              <input
                type="checkbox"
                checked={groupIds.includes(g.id)}
                disabled={readOnly}
                onChange={(e) =>
                  setGroupIds((ids) =>
                    e.target.checked ? [...ids, g.id] : ids.filter((id) => id !== g.id),
                  )
                }
              />
              {g.name}
            </label>
          ))}
        {inherited.length > 0 && category && (
          <p className="menu-panel__note">
            {t('admin.menu.groupsInherited', {
              category: category.name,
              groups: inherited.map((g) => g.name).join(', '),
            })}
          </p>
        )}
      </fieldset>

      <label className="menu-panel__check">
        <input
          type="checkbox"
          checked={active}
          disabled={readOnly}
          onChange={(e) => setActive(e.target.checked)}
        />
        {t('admin.menu.active')}
      </label>
      <Field
        label={t('admin.menu.erpCode')}
        value={erpCode}
        onChange={(e) => setErpCode(e.target.value)}
        disabled={readOnly}
      />

      {errors.form && (
        <p className="menu-panel__error" role="alert">
          {errors.form}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {t('admin.menu.save')}
      </Button>
      <Button block onClick={onCancel}>
        {t('common.cancel')}
      </Button>

      {item && (history.data?.length ?? 0) > 0 && (
        <section className="menu-panel__history">
          <h3>{t('admin.menu.priceHistory')}</h3>
          <ul>
            {history.data!.map((h, i) => (
              <li key={i}>
                {t('admin.menu.historyEntry', {
                  when: formatDayTime(h.at),
                  list: h.priceListName ?? h.optionName ?? '',
                  old: h.oldPrice ?? '–',
                  new: h.newPrice ?? '–',
                  who: h.approvedBy ? `${h.changedBy} (approved by ${h.approvedBy})` : h.changedBy,
                })}
              </li>
            ))}
          </ul>
        </section>
      )}

      {confirmZero && (
        <Modal title={t('admin.menu.zeroTitle')} onClose={() => setConfirmZero(false)}>
          <div className="menu-confirm">
            <p>{t('admin.menu.zeroText')}</p>
            <Button variant="danger" size="lg" block onClick={() => void save(true)}>
              {t('admin.menu.zeroConfirm')}
            </Button>
            <Button block onClick={() => setConfirmZero(false)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}
    </form>
  );
}
