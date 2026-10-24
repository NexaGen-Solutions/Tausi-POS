import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useBlocker } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, Modal, Table, useToast, type Column } from '../../shared/ui';
import { changedValues, toEdit, type Edit } from './form';
import { LogoField } from './LogoField';
import { ReceiptPreviewPanel } from './ReceiptPreviewPanel';
import { SettingField } from './SettingField';
import { settingsKey, type SettingChange, type SettingEntry, type Settings } from './types';
import './settings.css';

/**
 * Wireframe 18: business details on the left, the Rules switches and the other settings on the
 * right. Changes are kept in the form until Save changes; leaving with unsaved changes asks first.
 */
export function SettingsPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const settings = useQuery({
    queryKey: settingsKey,
    queryFn: () => api<Settings>('/api/admin/settings'),
  });
  const [edits, setEdits] = useState<Record<string, Edit>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [history, setHistory] = useState(false);

  const entries = useMemo(
    () => settings.data?.sections.flatMap((s) => s.entries) ?? [],
    [settings.data],
  );
  const values = useMemo(() => changedValues(entries, edits), [entries, edits]);
  const dirty = Object.keys(values).length > 0;

  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      dirty && currentLocation.pathname !== nextLocation.pathname,
  );
  useEffect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  if (!settings.data)
    return <main className="settings">{settings.error ? t('common.somethingWrong') : null}</main>;
  const data = settings.data;

  const edit = (entry: SettingEntry): Edit => edits[entry.key] ?? toEdit(entry, entry.value);
  const change = (key: string, value: Edit) => {
    setEdits((e) => ({ ...e, [key]: value }));
    setErrors((current) => Object.fromEntries(Object.entries(current).filter(([k]) => k !== key)));
  };
  const saved = (next: Settings) => queries.setQueryData(settingsKey, next);

  const save = async () => {
    setBusy(true);
    setErrors({});
    try {
      saved(
        await api<Settings>('/api/admin/settings', {
          method: 'PUT',
          body: { values, version: data.version },
        }),
      );
      setEdits({});
      setConflict(false);
      toast(t('admin.settings.saved'));
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) setConflict(true);
      else if (error instanceof ApiError && error.problem.fieldErrors)
        setErrors(error.problem.fieldErrors);
      else
        toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
          tone: 'warning',
        });
    } finally {
      setBusy(false);
    }
  };

  // Reloads the saved values; what was typed stays in the form on top of them.
  const reload = async () => {
    setConflict(false);
    await queries.invalidateQueries({ queryKey: settingsKey });
  };

  const field = (entry: SettingEntry, labelValues?: Record<string, string | number>) => (
    <SettingField
      key={entry.key}
      entry={entry}
      edit={edit(entry)}
      changed={entry.key in values}
      error={errors[entry.key]}
      labelValues={labelValues}
      onChange={(value) => change(entry.key, value)}
    />
  );
  const section = (key: string) => data.sections.find((s) => s.key === key);
  const idleMinutes = entries.find((e) => e.key === 'pos.idleLogoutMinutes');

  return (
    <main className={`settings${previewing ? ' settings--preview' : ''}`}>
      <div className="settings__main">
        <div className="settings__header">
          <h1 className="admin__title">{t('admin.settings.title')}</h1>
          <div className="settings__buttons">
            <Button variant="link" onClick={() => setHistory(true)}>
              {t('admin.settings.history')}
            </Button>
            <Button onClick={() => setPreviewing((p) => !p)} aria-pressed={previewing}>
              {t('admin.settings.preview')}
            </Button>
            <Button variant="primary" disabled={!dirty || busy} onClick={() => void save()}>
              {t('admin.settings.save')}
            </Button>
          </div>
        </div>
        {conflict && (
          <Banner action={{ label: t('common.reload'), onClick: () => void reload() }}>
            {t('admin.settings.conflict')}
          </Banner>
        )}
        {Object.keys(errors).length > 0 && (
          <p className="nx-field__error" role="alert">
            {t('admin.settings.fixFields')}
          </p>
        )}
        <div className="settings__columns">
          <section className="settings-panel" aria-labelledby="settings-business">
            <h2 id="settings-business" className="settings-panel__title">
              {section('business')?.title}
            </h2>
            <p className="settings-panel__help">{t('admin.settings.businessHelp')}</p>
            {section('business')?.entries.map((entry) =>
              entry.key === 'business.logo' ? (
                <LogoField key={entry.key} settings={data} onSaved={saved} />
              ) : (
                field(entry)
              ),
            )}
          </section>
          <div className="settings__right">
            {data.sections
              .filter((s) => s.key !== 'business')
              .map((s) => (
                <section
                  key={s.key}
                  className="settings-panel"
                  aria-labelledby={`settings-${s.key}`}
                >
                  <h2 id={`settings-${s.key}`} className="settings-panel__title">
                    {s.title}
                  </h2>
                  {s.entries.map((entry) =>
                    field(
                      entry,
                      entry.key === 'pos.idleLogoutEnabled' && idleMinutes
                        ? { minutes: String(edit(idleMinutes)) }
                        : undefined,
                    ),
                  )}
                </section>
              ))}
          </div>
        </div>
      </div>
      {previewing && <ReceiptPreviewPanel values={values} onClose={() => setPreviewing(false)} />}
      {history && <HistoryDialog onClose={() => setHistory(false)} />}
      {blocker.state === 'blocked' && (
        <Modal title={t('admin.settings.leaveTitle')} onClose={() => blocker.reset()} width={460}>
          <p>{t('admin.settings.leaveText')}</p>
          <div className="settings-dialog__buttons">
            <Button onClick={() => blocker.reset()}>{t('admin.settings.stay')}</Button>
            <Button variant="danger" onClick={() => blocker.proceed()}>
              {t('admin.settings.leave')}
            </Button>
          </div>
        </Modal>
      )}
    </main>
  );
}

function HistoryDialog({ onClose }: { onClose: () => void }) {
  const history = useQuery({
    queryKey: ['settings', 'history'],
    queryFn: () => api<SettingChange[]>('/api/admin/settings/history'),
  });
  const columns: Column<SettingChange>[] = [
    { key: 'at', header: t('admin.settings.when'), render: (c) => formatDayTime(c.at) },
    { key: 'who', header: t('admin.settings.who'), render: (c) => c.who },
    { key: 'label', header: t('admin.settings.setting'), render: (c) => c.label },
    { key: 'old', header: t('admin.settings.old'), mono: true, render: (c) => c.old ?? '—' },
    { key: 'new', header: t('admin.settings.new'), mono: true, render: (c) => c.new ?? '—' },
  ];
  return (
    <Modal title={t('admin.settings.historyTitle')} onClose={onClose} width={860}>
      {history.data && history.data.length === 0 && <p>{t('admin.settings.noHistory')}</p>}
      {history.data && history.data.length > 0 && (
        <Table
          caption={t('admin.settings.historyTitle')}
          columns={columns}
          rows={history.data}
          rowKey={(c) => `${c.at}-${c.key}`}
        />
      )}
      <div className="settings-dialog__buttons">
        <Button onClick={onClose}>{t('common.close')}</Button>
      </div>
    </Modal>
  );
}
