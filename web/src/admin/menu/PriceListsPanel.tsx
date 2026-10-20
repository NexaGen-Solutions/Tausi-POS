import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Toggle } from '../../shared/ui';
import type { AdminMenu, PriceList } from './types';

/** Price lists: add, rename, (de)activate, and choose the default. */
export function PriceListsPanel({
  menu,
  onChanged,
  onStale,
}: {
  menu: AdminMenu;
  onChanged: () => void;
  onStale: () => void;
}) {
  const [newName, setNewName] = useState('');
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameTo, setRenameTo] = useState('');
  const [error, setError] = useState<string | null>(null);

  const run = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
      onChanged();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) onStale();
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };

  const save = (list: PriceList, change: Partial<PriceList>) =>
    run(() =>
      api(`/api/admin/menu/price-lists/${list.id}`, {
        method: 'PUT',
        body: {
          name: change.name ?? list.name,
          active: change.active ?? list.active,
          rowVersion: list.rowVersion,
        },
      }),
    );

  return (
    <div className="menu-panel__form">
      <ul className="menu-list">
        {menu.priceLists.map((list) => (
          <li key={list.id} className={list.active ? undefined : 'menu-list__inactive'}>
            {renaming === list.id ? (
              <form
                className="menu-list__rename"
                onSubmit={(e) => {
                  e.preventDefault();
                  setRenaming(null);
                  void save(list, { name: renameTo });
                }}
              >
                <Field
                  label={t('admin.menu.lists.rename')}
                  value={renameTo}
                  onChange={(e) => setRenameTo(e.target.value)}
                  autoFocus
                />
                <Button type="submit">{t('common.save')}</Button>
              </form>
            ) : (
              <span>
                <strong>{list.name}</strong>
                {list.isDefault && (
                  <span className="menu-tag">{t('admin.menu.lists.default')}</span>
                )}
              </span>
            )}
            {menu.canEdit && renaming !== list.id && (
              <span className="menu-list__actions">
                <Toggle
                  label={t('admin.menu.lists.activeLabel')}
                  checked={list.active}
                  disabled={list.isDefault}
                  onChange={(on) => void save(list, { active: on })}
                />
                <Button
                  variant="link"
                  onClick={() => {
                    setRenaming(list.id);
                    setRenameTo(list.name);
                  }}
                >
                  {t('admin.menu.lists.rename')}
                </Button>
                {!list.isDefault && list.active && (
                  <Button
                    variant="link"
                    onClick={() =>
                      void run(() =>
                        api(`/api/admin/menu/price-lists/${list.id}/make-default`, {
                          method: 'POST',
                        }),
                      )
                    }
                  >
                    {t('admin.menu.lists.makeDefault')}
                  </Button>
                )}
              </span>
            )}
          </li>
        ))}
      </ul>
      {menu.canEdit && (
        <form
          className="menu-list__rename"
          onSubmit={(e) => {
            e.preventDefault();
            if (!newName.trim()) return;
            void run(() =>
              api('/api/admin/menu/price-lists', {
                method: 'POST',
                body: { name: newName.trim(), active: true },
              }),
            ).then(() => setNewName(''));
          }}
        >
          <Field
            label={t('admin.menu.lists.newName')}
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
          />
          <Button type="submit" variant="primary">
            {t('admin.menu.lists.add')}
          </Button>
        </form>
      )}
      {error && (
        <p className="menu-panel__error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
