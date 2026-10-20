import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field } from '../../shared/ui';
import { choiceText, parsePrice, type AdminMenu, type MenuGroup, type SavedRef } from './types';

interface DraftOption {
  id: string | null;
  key: string;
  name: string;
  addOn: string;
  active: boolean;
}

let nextKey = 1;

/** Option groups: list them, add or edit one (options, add-ons, where it applies). */
export function GroupsPanel({
  menu,
  onChanged,
  onStale,
}: {
  menu: AdminMenu;
  onChanged: () => void;
  onStale: () => void;
}) {
  const [editing, setEditing] = useState<MenuGroup | 'new' | null>(null);

  if (editing)
    return (
      <GroupForm
        key={editing === 'new' ? 'new' : editing.id}
        menu={menu}
        group={editing === 'new' ? undefined : editing}
        onDone={() => {
          setEditing(null);
          onChanged();
        }}
        onStale={onStale}
        onBack={() => setEditing(null)}
      />
    );

  return (
    <div className="menu-panel__form">
      <ul className="menu-list">
        {menu.groups.map((g) => (
          <li key={g.id} className={g.active ? undefined : 'menu-list__inactive'}>
            <span>
              <strong>{g.name}</strong> ·{' '}
              {t('admin.menu.groups.choice', { text: choiceText(g.minSelect, g.maxSelect) })}
              <br />
              <small>
                {g.options
                  .filter((o) => o.active)
                  .map((o) => o.name)
                  .join(', ')}
              </small>
            </span>
            {menu.canEdit || menu.canEditPrices ? (
              <Button
                variant="link"
                aria-label={`${t('common.edit')} ${g.name}`}
                onClick={() => setEditing(g)}
              >
                {t('common.edit')}
              </Button>
            ) : null}
          </li>
        ))}
      </ul>
      {menu.canEdit && (
        <Button variant="primary" block onClick={() => setEditing('new')}>
          {t('admin.menu.groups.add')}
        </Button>
      )}
    </div>
  );
}

function GroupForm({
  menu,
  group,
  onDone,
  onStale,
  onBack,
}: {
  menu: AdminMenu;
  group?: MenuGroup;
  onDone: () => void;
  onStale: () => void;
  onBack: () => void;
}) {
  const [name, setName] = useState(group?.name ?? '');
  const [min, setMin] = useState(String(group?.minSelect ?? 1));
  const [max, setMax] = useState(String(group?.maxSelect ?? 1));
  const [active, setActive] = useState(group?.active ?? true);
  const [options, setOptions] = useState<DraftOption[]>(
    group?.options.map((o) => ({
      id: o.id,
      key: o.id,
      name: o.name,
      addOn: String(o.priceAddOn),
      active: o.active,
    })) ?? [{ id: null, key: `n${nextKey++}`, name: '', addOn: '0', active: true }],
  );
  const [categoryIds, setCategoryIds] = useState<string[]>(group?.categoryIds ?? []);
  const [itemIds, setItemIds] = useState<string[]>(group?.itemIds ?? []);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const readOnly = !menu.canEdit;
  const firstItem =
    menu.items.find((i) => i.active && itemIds.includes(i.id)) ??
    menu.items.find((i) => i.active && categoryIds.includes(i.categoryId));
  const preview = firstItem
    ? t('admin.menu.groups.preview', {
        item: firstItem.name,
        group: name || '…',
        choice: t('admin.menu.groups.choice', { text: choiceText(Number(min), Number(max)) }),
      })
    : t('admin.menu.groups.previewNone');

  const update = (key: string, change: Partial<DraftOption>) =>
    setOptions((list) => list.map((o) => (o.key === key ? { ...o, ...change } : o)));
  const move = (index: number, by: number) =>
    setOptions((list) => {
      const next = [...list];
      const [moved] = next.splice(index, 1);
      next.splice(index + by, 0, moved);
      return next;
    });

  const save = async () => {
    setError(null);
    const named = options.filter((o) => o.name.trim());
    if (name.trim().length < 2) return setError(t('admin.menu.errors.name'));
    if (named.some((o) => Number.isNaN(parsePrice(o.addOn)) || (parsePrice(o.addOn) ?? 0) < 0))
      return setError(t('admin.menu.errors.price'));
    setBusy(true);
    try {
      let ref: SavedRef = { id: group?.id ?? '', rowVersion: group?.rowVersion ?? 0 };
      if (menu.canEdit) {
        ref = await api<SavedRef>(
          group ? `/api/admin/menu/groups/${group.id}` : '/api/admin/menu/groups',
          {
            method: group ? 'PUT' : 'POST',
            body: {
              name: name.trim(),
              minSelect: Number(min),
              maxSelect: Number(max),
              sortOrder: group?.sortOrder ?? (menu.groups.length + 1) * 10,
              active,
              options: named.map((o, i) => ({
                id: o.id,
                name: o.name.trim(),
                sortOrder: (i + 1) * 10,
                active: o.active,
              })),
              categoryIds,
              itemIds,
              rowVersion: group?.rowVersion ?? 0,
            },
          },
        );
      }
      if (menu.canEditPrices) {
        // New options got their ids on save: read them back to set their add-ons.
        const fresh = await api<AdminMenu>('/api/admin/menu');
        const saved = fresh.groups.find((g) => g.id === ref.id);
        const addOns: Record<string, number> = {};
        for (const o of named) {
          const match = saved?.options.find((s) =>
            o.id ? s.id === o.id : s.name === o.name.trim(),
          );
          const value = parsePrice(o.addOn) ?? 0;
          if (match && match.priceAddOn !== value) addOns[match.id] = value;
        }
        if (saved && Object.keys(addOns).length > 0)
          await api(`/api/admin/menu/groups/${saved.id}/prices`, {
            method: 'PUT',
            body: { addOns, rowVersion: saved.rowVersion },
          });
      }
      onDone();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        setError(t('admin.menu.stale'));
        onStale();
      } else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const toggle = (list: string[], id: string, on: boolean) =>
    on ? [...list, id] : list.filter((x) => x !== id);

  return (
    <form
      className="menu-panel__form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <Button variant="link" onClick={onBack}>
        {t('admin.menu.groups.back')}
      </Button>
      <Field
        label={t('admin.menu.groups.name')}
        value={name}
        onChange={(e) => setName(e.target.value)}
        disabled={readOnly}
      />
      <div className="menu-panel__row">
        <Field
          label={t('admin.menu.groups.min')}
          type="number"
          min={0}
          value={min}
          onChange={(e) => setMin(e.target.value)}
          disabled={readOnly}
        />
        <Field
          label={t('admin.menu.groups.max')}
          type="number"
          min={1}
          value={max}
          onChange={(e) => setMax(e.target.value)}
          disabled={readOnly}
        />
      </div>
      <fieldset className="menu-panel__options">
        <legend>{t('admin.menu.groups.options')}</legend>
        {options.map((o, i) => (
          <div key={o.key} className="menu-option">
            <Field
              label={t('admin.menu.groups.option')}
              value={o.name}
              onChange={(e) => update(o.key, { name: e.target.value })}
              disabled={readOnly}
            />
            <Field
              label={t('admin.menu.groups.addOn')}
              className="menu-panel__money"
              inputMode="decimal"
              value={o.addOn}
              readOnly={!menu.canEditPrices}
              onChange={(e) => update(o.key, { addOn: e.target.value })}
            />
            <label className="menu-panel__check">
              <input
                type="checkbox"
                checked={o.active}
                disabled={readOnly}
                onChange={(e) => update(o.key, { active: e.target.checked })}
              />
              {t('admin.menu.active')}
            </label>
            <div className="menu-option__move">
              <Button
                aria-label={`${t('common.up')}: ${o.name}`}
                disabled={readOnly || i === 0}
                onClick={() => move(i, -1)}
              >
                ↑
              </Button>
              <Button
                aria-label={`${t('common.down')}: ${o.name}`}
                disabled={readOnly || i === options.length - 1}
                onClick={() => move(i, 1)}
              >
                ↓
              </Button>
            </div>
          </div>
        ))}
        {!readOnly && (
          <Button
            onClick={() =>
              setOptions((list) => [
                ...list,
                { id: null, key: `n${nextKey++}`, name: '', addOn: '0', active: true },
              ])
            }
          >
            {t('admin.menu.groups.addOption')}
          </Button>
        )}
      </fieldset>
      <fieldset className="menu-panel__groups">
        <legend>{t('admin.menu.groups.appliesTo')}</legend>
        {menu.categories
          .filter((c) => c.active)
          .map((c) => (
            <label key={c.id} className="menu-panel__check">
              <input
                type="checkbox"
                checked={categoryIds.includes(c.id)}
                disabled={readOnly}
                onChange={(e) => setCategoryIds((l) => toggle(l, c.id, e.target.checked))}
              />
              {c.name}
            </label>
          ))}
      </fieldset>
      <fieldset className="menu-panel__groups menu-panel__items">
        <legend>{t('admin.menu.groups.appliesToItems')}</legend>
        {menu.items
          .filter((i) => i.active)
          .map((i) => (
            <label key={i.id} className="menu-panel__check">
              <input
                type="checkbox"
                checked={itemIds.includes(i.id)}
                disabled={readOnly}
                onChange={(e) => setItemIds((l) => toggle(l, i.id, e.target.checked))}
              />
              {i.name}
            </label>
          ))}
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
      <p className="menu-panel__preview">{preview}</p>
      {error && (
        <p className="menu-panel__error" role="alert">
          {error}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {t('admin.menu.save')}
      </Button>
    </form>
  );
}
