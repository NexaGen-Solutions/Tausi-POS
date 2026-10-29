import { useState } from 'react';
import { ApiError, api, apiUrl } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { SavedRef } from './types';
import '../settings/settings.css';

const maxBytes = 2 * 1024 * 1024;
const types = ['image/png', 'image/jpeg'];

/** The item's picture on the order screen's tile: add, change or remove (saved at once). */
export function ItemPicture({
  itemId,
  imageFileId,
  disabled,
  onChanged,
}: {
  itemId: string;
  imageFileId: string | null;
  disabled: boolean;
  /** Saved: the item's new row version, and its picture now. */
  onChanged: (ref: SavedRef, imageFileId: string | null) => void;
}) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<SavedRef>, picture: () => Promise<string | null>) => {
    setBusy(true);
    setError(null);
    try {
      const ref = await action();
      onChanged(ref, await picture());
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  // The new picture's id comes with the menu; read it back for this item.
  const current = async () =>
    (
      await api<{ items: { id: string; imageFileId: string | null }[] }>('/api/admin/menu')
    ).items.find((i) => i.id === itemId)?.imageFileId ?? null;

  const upload = (file: File | undefined) => {
    if (!file) return;
    if (!types.includes(file.type)) return setError(t('admin.menu.picture.type'));
    if (file.size > maxBytes) return setError(t('admin.menu.picture.size'));
    const form = new FormData();
    form.append('file', file);
    void run(
      () => api<SavedRef>(`/api/admin/menu/items/${itemId}/image`, { method: 'POST', body: form }),
      current,
    );
  };

  return (
    <div className="nx-field settings-logo">
      <span className="nx-field__label">{t('admin.menu.picture.label')}</span>
      <div className="settings-logo__row">
        <div className="settings-logo__box">
          {imageFileId ? (
            <img src={apiUrl(`/files/${imageFileId}`)} alt={t('admin.menu.picture.label')} />
          ) : (
            <span>{t('admin.menu.picture.none')}</span>
          )}
        </div>
        <div className="settings-logo__actions">
          <label
            className={`nx-btn nx-btn--secondary nx-btn--md${busy || disabled ? ' settings-busy' : ''}`}
          >
            {imageFileId ? t('admin.menu.picture.change') : t('admin.menu.picture.add')}
            <input
              type="file"
              accept="image/png,image/jpeg"
              className="visually-hidden"
              disabled={busy || disabled}
              onChange={(e) => {
                upload(e.target.files?.[0]);
                e.target.value = '';
              }}
            />
          </label>
          {imageFileId && (
            <Button
              disabled={busy || disabled}
              onClick={() =>
                void run(
                  () =>
                    api<SavedRef>(`/api/admin/menu/items/${itemId}/image`, { method: 'DELETE' }),
                  async () => null,
                )
              }
            >
              {t('admin.menu.picture.remove')}
            </Button>
          )}
          <span className="nx-field__hint">{t('admin.menu.picture.hint')}</span>
        </div>
      </div>
      {error && (
        <span className="nx-field__error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}
