import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField, Toggle } from '../../shared/ui';
import type { ImportPreview, ImportResult } from './types';

interface Options {
  mapping: string[] | null;
  updateExisting: boolean;
  deactivateMissing: boolean;
  skipErrors: boolean;
}

/** CSV import: choose a file, match columns if needed, preview, confirm, apply (all or nothing). */
export function ImportDialog({
  onClose,
  onApplied,
}: {
  onClose: () => void;
  onApplied: () => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [options, setOptions] = useState<Options>({
    mapping: null,
    updateExisting: true,
    deactivateMissing: false,
    skipErrors: false,
  });
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [mapping, setMapping] = useState<string[]>([]);
  const [confirming, setConfirming] = useState(false);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const form = (chosen: File, opts: Options) => {
    const data = new FormData();
    data.append('file', chosen);
    data.append('options', JSON.stringify(opts));
    return data;
  };

  const runPreview = async (chosen: File, opts: Options) => {
    setBusy(true);
    setError(null);
    try {
      const p = await api<ImportPreview>('/api/admin/menu/import/preview', {
        method: 'POST',
        body: form(chosen, opts),
      });
      setPreview(p);
      setMapping(p.mapping);
      setOptions(opts);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const apply = async () => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      setResult(
        await api<ImportResult>('/api/admin/menu/import/apply', {
          method: 'POST',
          body: form(file, options),
        }),
      );
      setConfirming(false);
      onApplied();
    } catch (e) {
      setConfirming(false);
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const change = (next: Partial<Options>) => {
    const opts = { ...options, ...next };
    setOptions(opts);
    if (file) void runPreview(file, opts);
  };

  const changedRows = preview?.rows.filter((r) => r.status !== 'Unchanged') ?? [];
  const blocked = !!preview && preview.errors > 0 && !options.skipErrors;

  return (
    <Modal title={t('admin.menu.import.title')} onClose={onClose} width={980}>
      <div className="menu-import">
        {result ? (
          <>
            <p role="status">
              {t('admin.menu.import.done', {
                new: result.new,
                updated: result.updated,
                prices: result.priceChanges,
              })}
            </p>
            <Button variant="primary" size="lg" onClick={onClose}>
              {t('admin.menu.import.close')}
            </Button>
          </>
        ) : (
          <>
            <Field
              label={t('admin.menu.import.choose')}
              type="file"
              accept=".csv,text/csv"
              hint={t('admin.menu.import.chooseHint')}
              onChange={(e) => {
                const chosen = e.target.files?.[0] ?? null;
                setFile(chosen);
                setPreview(null);
                setResult(null);
                if (chosen) void runPreview(chosen, { ...options, mapping: null });
              }}
            />

            {preview?.needsMapping && (
              <section className="menu-import__mapping">
                <h3>{t('admin.menu.import.mapTitle')}</h3>
                <p className="menu-panel__note">
                  {preview.mappingProblem ?? t('admin.menu.import.mapHint')}
                </p>
                <div className="menu-import__map-grid">
                  {preview.headers.map((header, i) => (
                    <SelectField
                      key={`${header}-${i}`}
                      label={header || `Column ${i + 1}`}
                      value={mapping[i] ?? 'ignore'}
                      onChange={(e) =>
                        setMapping((m) => m.map((v, j) => (j === i ? e.target.value : v)))
                      }
                      options={preview.fields.map((f) => ({ value: f.field, label: f.label }))}
                    />
                  ))}
                </div>
                <Button
                  variant="primary"
                  disabled={busy}
                  onClick={() => file && void runPreview(file, { ...options, mapping })}
                >
                  {t('admin.menu.import.previewButton')}
                </Button>
              </section>
            )}

            {preview && !preview.needsMapping && (
              <section className="menu-import__preview" aria-label="Preview">
                <p className="menu-import__summary">
                  {t('admin.menu.import.summary', {
                    new: preview.new,
                    updated: preview.updated,
                    unchanged: preview.unchanged,
                    errors: preview.errors,
                  })}
                </p>
                {preview.newCategories.length > 0 && (
                  <p>
                    {t('admin.menu.import.newCategories', {
                      names: preview.newCategories.join(', '),
                    })}
                  </p>
                )}
                {preview.toDeactivate.length > 0 && (
                  <p>
                    {t('admin.menu.import.deactivating', { count: preview.toDeactivate.length })}
                  </p>
                )}
                <div className="menu-import__options">
                  <Toggle
                    label={t('admin.menu.import.updateExisting')}
                    checked={options.updateExisting}
                    onChange={(on) => change({ updateExisting: on })}
                  />
                  <Toggle
                    label={t('admin.menu.import.deactivateMissing')}
                    checked={options.deactivateMissing}
                    onChange={(on) => change({ deactivateMissing: on })}
                  />
                  <Toggle
                    label={t('admin.menu.import.skipErrors')}
                    checked={options.skipErrors}
                    onChange={(on) => change({ skipErrors: on })}
                  />
                </div>
                <div className="nx-table-box">
                  <table className="nx-table" aria-label="Import preview">
                    <thead>
                      <tr>
                        <th scope="col">{t('admin.menu.import.colLine')}</th>
                        <th scope="col">{t('admin.menu.import.colStatus')}</th>
                        <th scope="col">{t('admin.menu.colName')}</th>
                        <th scope="col">{t('admin.menu.colCategory')}</th>
                        <th scope="col">{t('admin.menu.import.colChanges')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {changedRows.map((row) => (
                        <tr
                          key={row.line}
                          className={`menu-import__row--${row.status.toLowerCase()}`}
                        >
                          <td className="nx-table--mono">{row.line}</td>
                          <td>{row.status}</td>
                          <td>{row.name}</td>
                          <td>{row.category}</td>
                          <td>
                            {row.error ?? (
                              <ul className="menu-import__changes">
                                {row.changes.map((c, i) => (
                                  <li
                                    key={i}
                                    className={c.isPrice ? 'menu-import__price' : undefined}
                                  >
                                    {c.field}: {c.old ?? '–'} → {c.new ?? '–'}
                                  </li>
                                ))}
                              </ul>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                {preview.unchanged > 0 && (
                  <p className="menu-panel__note">
                    {t('admin.menu.import.unchangedHidden', { count: preview.unchanged })}
                  </p>
                )}
                {blocked && (
                  <p className="menu-panel__error">{t('admin.menu.import.errorsBlock')}</p>
                )}
                <Button
                  variant="primary"
                  size="lg"
                  disabled={busy || blocked}
                  onClick={() => setConfirming(true)}
                >
                  {t('admin.menu.import.apply')}
                </Button>
              </section>
            )}
            {error && (
              <p className="menu-panel__error" role="alert">
                {error}
              </p>
            )}
          </>
        )}
      </div>
      {confirming && preview && (
        <Modal title={t('admin.menu.import.confirmTitle')} onClose={() => setConfirming(false)}>
          <div className="menu-confirm">
            <p>
              {t('admin.menu.import.confirmText', {
                new: preview.new,
                updated: preview.updated,
                prices: preview.priceChanges,
                deactivate: preview.toDeactivate.length
                  ? t('admin.menu.import.confirmDeactivate', { count: preview.toDeactivate.length })
                  : '',
              })}
            </p>
            <Button variant="primary" size="lg" block disabled={busy} onClick={() => void apply()}>
              {t('admin.menu.import.apply')}
            </Button>
            <Button block onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}
    </Modal>
  );
}
