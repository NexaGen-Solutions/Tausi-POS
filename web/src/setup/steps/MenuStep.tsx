import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api, apiFile, apiUrl } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, SelectField, Toggle } from '../../shared/ui';
import type { ImportPreview, ImportResult } from '../../admin/menu/types';
import { saveBlob } from '../files';
import { NextButton, type StepProps } from '../SetupWizard';
import { PinList } from './AdminStep';
import type { SambaConnection, SambaOptions, SambaPreview, SambaResult } from '../types';

const templateColumns = [
  'category',
  'name',
  'short_name',
  'price',
  'price_list_2_price',
  'vat_rate',
  'vat_exempt',
  'modifier_group',
  'erpnext_item_code',
  'active',
];

/** Step 4, Import the menu: from the template CSV, a CSV or Excel file, or SambaPOS; and quick tables. */
export function MenuStep({ onNext }: StepProps) {
  const [way, setWay] = useState<'file' | 'sambapos'>('file');
  const [summary, setSummary] = useState<string | null>(null);
  const done = async () => {
    await api('/api/setup/steps/menu/done', { method: 'POST', body: { summary } });
    onNext();
  };
  return (
    <div className="wizard__form">
      <div className="wizard__choices" role="group" aria-label={t('wizard.menu.how')}>
        <button
          type="button"
          className={`wizard__choice${way === 'file' ? ' wizard__choice--on' : ''}`}
          aria-pressed={way === 'file'}
          onClick={() => setWay('file')}
        >
          {t('wizard.menu.fromFile')}
        </button>
        <button
          type="button"
          className={`wizard__choice${way === 'sambapos' ? ' wizard__choice--on' : ''}`}
          aria-pressed={way === 'sambapos'}
          onClick={() => setWay('sambapos')}
        >
          {t('wizard.menu.fromSambaPos')}
        </button>
      </div>
      {way === 'file' ? (
        <FileImport onImported={setSummary} />
      ) : (
        <SambaImport onImported={setSummary} />
      )}
      {summary && (
        <p className="wizard__ok" role="status">
          {summary}{' '}
          <a href="/admin/menu" target="_blank" rel="noreferrer">
            {t('wizard.menu.review')}
          </a>
        </p>
      )}
      <QuickTables />
      <NextButton next="stock" onClick={() => void done()} />
    </div>
  );
}

function FileImport({ onImported }: { onImported: (summary: string) => void }) {
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [mapping, setMapping] = useState<string[] | null>(null);
  const [update, setUpdate] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [help, setHelp] = useState(false);

  const form = (chosen: File, map: string[] | null, updateExisting: boolean) => {
    const data = new FormData();
    data.append('file', chosen);
    data.append(
      'options',
      JSON.stringify({ mapping: map, updateExisting, deactivateMissing: false, skipErrors: true }),
    );
    return data;
  };
  const run = async (chosen: File, map: string[] | null, updateExisting: boolean) => {
    setBusy(true);
    setError(null);
    try {
      const p = await api<ImportPreview>('/api/admin/menu/import/preview', {
        method: 'POST',
        body: form(chosen, map, updateExisting),
      });
      setPreview(p);
      setMapping(p.mapping);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  const pick = (chosen: File | undefined) => {
    if (!chosen) return;
    if (chosen.size > 2 * 1024 * 1024) return setError(t('wizard.menu.tooBig'));
    setFile(chosen);
    void run(chosen, null, update);
  };
  const apply = async () => {
    if (!file) return;
    setBusy(true);
    try {
      const r = await api<ImportResult>('/api/admin/menu/import/apply', {
        method: 'POST',
        body: form(file, mapping, update),
      });
      onImported(
        t('wizard.menu.imported', {
          new: r.new,
          updated: r.updated,
          skipped: r.skipped,
          existing: r.existing,
        }),
      );
      setPreview(null);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  const skippedRows = async () => {
    if (!file) return;
    const f = await apiFile('/api/admin/menu/import/skipped', {
      method: 'POST',
      body: form(file, mapping, update),
      readOnly: true,
    });
    saveBlob(f.blob, f.fileName);
  };

  return (
    <section className="wizard__panel" aria-label={t('wizard.menu.fromFile')}>
      <div className="wizard__row">
        <a
          className="nx-btn nx-btn--secondary nx-btn--md"
          href={apiUrl('/api/admin/menu/import/template')}
          download
        >
          {t('wizard.menu.template')}
        </a>
        <Button variant="link" aria-expanded={help} onClick={() => setHelp((h) => !h)}>
          {t('wizard.menu.help')}
        </Button>
        <label className="nx-btn nx-btn--primary nx-btn--md wizard__upload">
          {t('wizard.menu.upload')}
          <input
            type="file"
            accept=".csv,.xlsx,text/csv"
            className="visually-hidden"
            onChange={(e) => pick(e.target.files?.[0])}
          />
        </label>
      </div>
      {help && (
        <dl className="wizard__help">
          {templateColumns.map((c) => (
            <div key={c}>
              <dt className="mono">{c}</dt>
              <dd>{t(`wizard.menu.columns.${c}`)}</dd>
            </div>
          ))}
        </dl>
      )}
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      {preview && mapping && file && (
        <>
          <div className="wizard__mapping">
            {preview.headers.map((h, i) => (
              <SelectField
                key={`${h}-${i}`}
                label={h || t('wizard.menu.column', { n: i + 1 })}
                value={mapping[i]}
                onChange={(e) => {
                  const next = mapping.map((m, j) => (j === i ? e.target.value : m));
                  setMapping(next);
                  void run(file, next, update);
                }}
                options={preview.fields.map((f) => ({ value: f.field, label: f.label }))}
              />
            ))}
          </div>
          {preview.needsMapping ? (
            <p className="wizard__warn">{preview.mappingProblem}</p>
          ) : (
            <>
              <p role="status" className="wizard__counts">
                {t('wizard.menu.counts', {
                  ready: preview.new + preview.updated,
                  errors: preview.errors,
                  existing: preview.existing,
                })}
              </p>
              <Toggle
                label={t('wizard.menu.updateExisting')}
                checked={update}
                onChange={(v) => {
                  setUpdate(v);
                  void run(file, mapping, v);
                }}
              />
              <div className="wizard__scroll">
                <table className="wizard__table" aria-label={t('wizard.menu.preview')}>
                  <thead>
                    <tr>
                      <th scope="col">{t('wizard.menu.line')}</th>
                      <th scope="col">{t('wizard.menu.category')}</th>
                      <th scope="col">{t('wizard.menu.item')}</th>
                      <th scope="col">{t('wizard.menu.check')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {preview.rows.slice(0, 200).map((r) => (
                      <tr
                        key={r.line}
                        className={r.status === 'Error' ? 'wizard__row--error' : undefined}
                      >
                        <td className="mono">{r.line}</td>
                        <td>{r.category}</td>
                        <td>{r.name}</td>
                        <td>
                          {r.status === 'Error'
                            ? r.error
                            : r.status === 'Existing'
                              ? t('wizard.menu.duplicate')
                              : '✓'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="wizard__row">
                <Button
                  variant="primary"
                  disabled={busy || preview.new + preview.updated === 0}
                  onClick={() => void apply()}
                >
                  {t('wizard.menu.import', { count: preview.new + preview.updated })}
                </Button>
                {preview.errors > 0 && (
                  <Button onClick={() => void skippedRows()}>{t('wizard.menu.skippedRows')}</Button>
                )}
              </div>
            </>
          )}
        </>
      )}
    </section>
  );
}

function SambaImport({ onImported }: { onImported: (summary: string) => void }) {
  const [connection, setConnection] = useState<SambaConnection>({
    server: '',
    database: 'SambaPOS5',
    windowsLogin: true,
    user: '',
    password: '',
  });
  const [options, setOptions] = useState<SambaOptions>({
    menu: true,
    prices: true,
    modifiers: true,
    tables: true,
    users: true,
    categoriesFromScreenMenu: false,
    userRoles: {},
  });
  const [message, setMessage] = useState<string | null>(null);
  const [preview, setPreview] = useState<SambaPreview | null>(null);
  const [result, setResult] = useState<SambaResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (key: keyof SambaConnection, value: string | boolean) =>
    setConnection((c) => ({ ...c, [key]: value }));
  const call = async <T,>(action: () => Promise<T>, then: (value: T) => void) => {
    setBusy(true);
    setError(null);
    try {
      then(await action());
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  const body = () => ({ connection, options });
  const parts: (keyof SambaOptions)[] = ['menu', 'prices', 'modifiers', 'tables', 'users'];

  return (
    <section className="wizard__panel" aria-label={t('wizard.menu.fromSambaPos')}>
      <p className="wizard__note">{t('wizard.samba.readOnly')}</p>
      <div className="wizard__grid">
        <Field
          label={t('wizard.samba.server')}
          placeholder="POSSERVER2\SQLEXPRESS"
          value={connection.server}
          onChange={(e) => set('server', e.target.value)}
        />
        <Field
          label={t('wizard.samba.database')}
          value={connection.database}
          onChange={(e) => set('database', e.target.value)}
        />
        <SelectField
          label={t('wizard.samba.login')}
          value={connection.windowsLogin ? 'windows' : 'sql'}
          onChange={(e) => set('windowsLogin', e.target.value === 'windows')}
          options={[
            { value: 'windows', label: t('wizard.samba.windows') },
            { value: 'sql', label: t('wizard.samba.sql') },
          ]}
        />
        {!connection.windowsLogin && (
          <>
            <Field
              label={t('wizard.samba.user')}
              value={connection.user}
              onChange={(e) => set('user', e.target.value)}
            />
            <Field
              label={t('wizard.samba.password')}
              type="password"
              value={connection.password}
              onChange={(e) => set('password', e.target.value)}
            />
          </>
        )}
      </div>
      <div className="wizard__row">
        <Button
          disabled={busy || !connection.server.trim()}
          onClick={() =>
            void call(
              () =>
                api<{ message: string }>('/api/admin/menu/import/sambapos/test', {
                  method: 'POST',
                  body: connection,
                }),
              (r) => setMessage(r.message),
            )
          }
        >
          {t('wizard.samba.test')}
        </Button>
        <Button
          variant="primary"
          disabled={busy || !connection.server.trim()}
          onClick={() =>
            void call(
              () =>
                api<SambaPreview>('/api/admin/menu/import/sambapos/preview', {
                  method: 'POST',
                  body: body(),
                  readOnly: true,
                }),
              setPreview,
            )
          }
        >
          {t('wizard.samba.preview')}
        </Button>
      </div>
      {message && (
        <p className="wizard__ok" role="status">
          {message}
        </p>
      )}
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      {preview && (
        <div className="wizard__samba">
          <div className="report-checks" role="group" aria-label={t('wizard.samba.bring')}>
            {parts.map((p) => (
              <label key={p} className="report-check">
                <input
                  type="checkbox"
                  checked={options[p] as boolean}
                  onChange={(e) => setOptions((o) => ({ ...o, [p]: e.target.checked }))}
                />
                {t(`wizard.samba.parts.${p}`)}
              </label>
            ))}
            <label className="report-check">
              <input
                type="checkbox"
                checked={options.categoriesFromScreenMenu}
                onChange={(e) =>
                  setOptions((o) => ({ ...o, categoriesFromScreenMenu: e.target.checked }))
                }
              />
              {t('wizard.samba.screenCategories')}
            </label>
          </div>
          <p role="status" className="wizard__counts">
            {t('wizard.samba.counts', {
              items: preview.newItems,
              existing: preview.existingItems,
              lists: preview.priceLists.length,
              groups: preview.modifierGroups.length,
              tables: preview.newTables,
              users: preview.newUsers,
            })}
          </p>
          {preview.problems.map((p) => (
            <p key={p} className="wizard__warn">
              {p}
            </p>
          ))}
          <dl className="wizard__help">
            <div>
              <dt>{t('wizard.samba.priceLists')}</dt>
              <dd>{preview.priceLists.join(', ')}</dd>
            </div>
            <div>
              <dt>{t('wizard.samba.modifiers')}</dt>
              <dd>{preview.modifierGroups.join(', ')}</dd>
            </div>
            <div>
              <dt>{t('wizard.samba.tables')}</dt>
              <dd>{preview.tables.join(', ')}</dd>
            </div>
          </dl>
          <div className="wizard__scroll">
            <table className="wizard__table" aria-label={t('wizard.samba.items')}>
              <thead>
                <tr>
                  <th scope="col">{t('wizard.menu.category')}</th>
                  <th scope="col">{t('wizard.menu.item')}</th>
                  <th scope="col">{t('wizard.samba.prices')}</th>
                  <th scope="col">{t('wizard.menu.check')}</th>
                </tr>
              </thead>
              <tbody>
                {preview.items.slice(0, 200).map((i) => (
                  <tr key={i.name}>
                    <td>{i.category}</td>
                    <td>{i.name}</td>
                    <td>{i.prices.join(' · ')}</td>
                    <td>{i.status === 'Existing' ? t('wizard.menu.duplicate') : '✓'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {preview.users.length > 0 && (
            <div className="wizard__users">
              {preview.users.map((u) => (
                <SelectField
                  key={u.id}
                  label={`${u.name}${u.sambaRole ? ` (${u.sambaRole})` : ''}${u.status === 'Existing' ? ` · ${t('wizard.samba.existing')}` : ''}`}
                  value={options.userRoles[u.id] ?? u.role ?? ''}
                  disabled={u.status === 'Existing'}
                  onChange={(e) =>
                    setOptions((o) => ({
                      ...o,
                      userRoles: { ...o.userRoles, [u.id]: e.target.value },
                    }))
                  }
                  options={[
                    { value: '', label: t('wizard.samba.chooseRole') },
                    ...['waiter', 'cashier', 'supervisor', 'admin'].map((r) => ({
                      value: r,
                      label: t(`wizard.admin.roles.${r}`),
                    })),
                  ]}
                />
              ))}
            </div>
          )}
          <Button
            variant="primary"
            disabled={busy}
            onClick={() =>
              void call(
                () =>
                  api<SambaResult>('/api/admin/menu/import/sambapos/apply', {
                    method: 'POST',
                    body: body(),
                  }),
                (r) => {
                  setResult(r);
                  setPreview(null);
                  onImported(r.summary);
                },
              )
            }
          >
            {t('wizard.samba.import')}
          </Button>
        </div>
      )}
      {result && <PinList people={result.people} pdf={result.pinSheetPdf} />}
    </section>
  );
}

/** "Add tables 1 to 16 in Main" (Task 5), when no tables came in. */
function QuickTables() {
  const tables = useQuery({
    queryKey: ['setup', 'tables'],
    queryFn: () =>
      api<{ areas: { id: string; name: string }[]; tables: unknown[] }>('/api/admin/tables'),
  });
  const [from, setFrom] = useState('1');
  const [to, setTo] = useState('16');
  const [area, setArea] = useState('Main');
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  if (!tables.data || tables.data.tables.length > 0) return null;
  const add = async () => {
    setError(null);
    try {
      let target = tables.data!.areas.find(
        (a) => a.name.toLowerCase() === area.trim().toLowerCase(),
      );
      if (!target) {
        await api('/api/admin/tables/areas', {
          method: 'POST',
          body: { name: area.trim(), sortOrder: 10, active: true, rowVersion: 0 },
        });
        const fresh = await api<{ areas: { id: string; name: string }[] }>('/api/admin/tables');
        target = fresh.areas.find((a) => a.name.toLowerCase() === area.trim().toLowerCase());
      }
      const r = await api<{ added: string[] }>('/api/admin/tables/quick-add', {
        method: 'POST',
        body: { from: Number(from), to: Number(to), areaId: target!.id, prefix: null },
      });
      setMessage(t('wizard.tables.added', { count: r.added.length, area: area.trim() }));
      void tables.refetch();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };
  return (
    <section className="wizard__panel" aria-labelledby="quick-tables">
      <h3 id="quick-tables" className="wizard__subtitle">
        {t('wizard.tables.title')}
      </h3>
      <div className="wizard__row">
        <Field
          label={t('wizard.tables.from')}
          value={from}
          inputMode="numeric"
          onChange={(e) => setFrom(e.target.value.replace(/\D/g, ''))}
        />
        <Field
          label={t('wizard.tables.to')}
          value={to}
          inputMode="numeric"
          onChange={(e) => setTo(e.target.value.replace(/\D/g, ''))}
        />
        <Field
          label={t('wizard.tables.area')}
          value={area}
          onChange={(e) => setArea(e.target.value)}
        />
        <Button onClick={() => void add()} disabled={!from || !to || !area.trim()}>
          {t('wizard.tables.add')}
        </Button>
      </div>
      {message && (
        <p className="wizard__ok" role="status">
          {message}
        </p>
      )}
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
