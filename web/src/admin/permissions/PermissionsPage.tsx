import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Fragment, useMemo, useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, Modal, Table, useToast, type Column } from '../../shared/ui';
import { approvers, cellId, isLocked, nextValue, valueOf } from './grid';
import { AddRoleDialog, DeleteRoleDialog, RenameRoleDialog } from './RoleDialogs';
import {
  permissionsKey,
  type PermissionChange,
  type PermissionValue,
  type PermissionsGrid,
  type RoleInfo,
} from './types';
import './permissions.css';

type Dialog =
  | { kind: 'add' }
  | { kind: 'rename'; role: RoleInfo }
  | { kind: 'delete'; role: RoleInfo }
  | { kind: 'history' };

const pillText = (value: PermissionValue) => t(`admin.permissions.value.${value}`);

/**
 * Wireframe 16: roles across, every permission down in sections. Tapping a cell steps through its
 * values; changes are staged (outlined) until Save changes. The Admin role keeps permissions.manage.
 */
export function PermissionsPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const grid = useQuery({
    queryKey: permissionsKey,
    queryFn: () => api<PermissionsGrid>('/api/admin/permissions'),
  });
  const [staged, setStaged] = useState<Record<string, PermissionValue>>({});
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [showApprovers, setShowApprovers] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const data = grid.data;
  // Only cells that differ from what is saved count as changes.
  const changes = useMemo(
    () =>
      data
        ? Object.entries(staged).filter(([id, value]) => {
            const [roleId, key] = id.split(':');
            return data.cells[roleId]?.[key] !== value;
          })
        : [],
    [data, staged],
  );
  const who = useMemo(() => (data ? approvers(data, staged) : []), [data, staged]);
  const nobody = who.filter((w) => w.nobody);

  if (!data)
    return <main className="permissions">{grid.error ? t('common.somethingWrong') : null}</main>;

  const sections = [...new Set(data.keys.map((k) => k.section))];
  const tap = (role: RoleInfo, key: string, approvable: boolean) => {
    const id = cellId(role.id, key);
    setStaged((s) => ({ ...s, [id]: nextValue(valueOf(data, s, role.id, key), approvable) }));
    setError(null);
  };

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const saved = await api<PermissionsGrid>('/api/admin/permissions', {
        method: 'PUT',
        body: {
          version: data.version,
          changes: changes.map(([id, value]) => {
            const [roleId, permissionKey] = id.split(':');
            return { roleId, permissionKey, value };
          }),
        },
      });
      queries.setQueryData(permissionsKey, saved);
      setStaged({});
      toast(t('admin.permissions.saved'));
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) setConflict(true);
      else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const roleSaved = (next: PermissionsGrid | null) => {
    if (next) queries.setQueryData(permissionsKey, next);
    setDialog(null);
  };

  return (
    <main className={`permissions${showApprovers ? ' permissions--panel' : ''}`}>
      <div className="permissions__main">
        <div className="permissions__header">
          <div>
            <h1 className="admin__title">{t('admin.permissions.title')}</h1>
            <p className="permissions__subtitle">{t('admin.permissions.subtitle')}</p>
          </div>
          <div className="permissions__legend" aria-hidden="true">
            {(['Allowed', 'NeedsApproval', 'NotAllowed'] as const).map((v) => (
              <span key={v} className={`permissions-pill permissions-pill--${v}`}>
                {pillText(v)}
              </span>
            ))}
          </div>
          <div className="permissions__buttons">
            <Button variant="link" onClick={() => setDialog({ kind: 'history' })}>
              {t('admin.permissions.history')}
            </Button>
            <Button aria-pressed={showApprovers} onClick={() => setShowApprovers((s) => !s)}>
              {t('admin.permissions.whoApproves')}
            </Button>
            <Button onClick={() => setDialog({ kind: 'add' })}>
              {t('admin.permissions.addRoleButton')}
            </Button>
          </div>
        </div>

        {conflict && (
          <Banner
            action={{
              label: t('common.reload'),
              onClick: () => {
                setConflict(false);
                void queries.invalidateQueries({ queryKey: permissionsKey });
              },
            }}
          >
            {t('admin.permissions.conflict')}
          </Banner>
        )}
        {nobody.map((w) => (
          <p key={w.info.key} className="permissions__warning" role="alert">
            {t('admin.permissions.nobody', { action: w.info.label })}
          </p>
        ))}
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}

        <div className="permissions__savebar">
          <Button disabled={changes.length === 0 || busy} onClick={() => setStaged({})}>
            {t('admin.permissions.discard')}
          </Button>
          <Button
            variant="primary"
            disabled={changes.length === 0 || busy || nobody.length > 0}
            onClick={() => void save()}
          >
            {t('admin.permissions.save', { count: changes.length })}
          </Button>
        </div>

        <div className="nx-table-box">
          <table className="nx-table permissions-table">
            <caption className="visually-hidden">{t('admin.permissions.title')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('admin.permissions.action')}</th>
                {data.roles.map((role) => (
                  <th key={role.id} scope="col">
                    <span className="permissions-role">{role.name}</span>
                    <span className="permissions-role__actions">
                      <Button
                        variant="link"
                        aria-label={t('admin.permissions.renameRole', { name: role.name })}
                        onClick={() => setDialog({ kind: 'rename', role })}
                      >
                        {t('admin.permissions.rename')}
                      </Button>
                      {role.systemKey === null && (
                        <Button
                          variant="link"
                          aria-label={t('admin.permissions.deleteRole', { name: role.name })}
                          onClick={() => setDialog({ kind: 'delete', role })}
                        >
                          {t('admin.permissions.delete')}
                        </Button>
                      )}
                    </span>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {sections.map((section) => (
                <Fragment key={section}>
                  <tr className="permissions-section">
                    <th scope="colgroup" colSpan={data.roles.length + 1}>
                      {t(`admin.permissions.sections.${section}`)}
                    </th>
                  </tr>
                  {data.keys
                    .filter((k) => k.section === section)
                    .map((k) => (
                      <tr key={k.key}>
                        <th scope="row" className="permissions-action">
                          <span className="permissions-action__label">{k.label}</span>
                          <span className="permissions-action__help">{k.help}</span>
                        </th>
                        {data.roles.map((role) => {
                          const value = valueOf(data, staged, role.id, k.key);
                          const locked = isLocked(role, k.key);
                          const changed = value !== data.cells[role.id]?.[k.key];
                          return (
                            <td key={role.id}>
                              <button
                                type="button"
                                className={`permissions-pill permissions-pill--${value}${changed ? ' permissions-pill--changed' : ''}`}
                                aria-label={`${role.name}: ${k.label}: ${pillText(value)}${locked ? `. ${t('admin.permissions.locked')}` : ''}`}
                                title={locked ? t('admin.permissions.locked') : undefined}
                                disabled={locked}
                                onClick={() => tap(role, k.key, k.approvable)}
                              >
                                {locked && <Padlock />}
                                {pillText(value)}
                              </button>
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {showApprovers && (
        <aside className="permissions-approvers" aria-label={t('admin.permissions.whoApproves')}>
          <h2 className="permissions-approvers__title">{t('admin.permissions.whoApproves')}</h2>
          <ul>
            {who.map((w) => (
              <li
                key={w.info.key}
                className={w.nobody ? 'permissions-approvers--warning' : undefined}
              >
                <span className="permissions-action__label">{w.info.label}</span>
                <span className="permissions-action__help">
                  {w.nobody
                    ? t('admin.permissions.nobody', { action: w.info.label })
                    : w.roles.length > 0
                      ? t('admin.permissions.approvedBy', { roles: w.roles.join(', ') })
                      : t('admin.permissions.nobodyAllowed')}
                </span>
              </li>
            ))}
          </ul>
        </aside>
      )}

      {dialog?.kind === 'add' && <AddRoleDialog grid={data} onDone={roleSaved} />}
      {dialog?.kind === 'rename' && <RenameRoleDialog role={dialog.role} onDone={roleSaved} />}
      {dialog?.kind === 'delete' && <DeleteRoleDialog role={dialog.role} onDone={roleSaved} />}
      {dialog?.kind === 'history' && <HistoryDialog onClose={() => setDialog(null)} />}
    </main>
  );
}

function Padlock() {
  return (
    <svg
      className="permissions-pill__lock"
      viewBox="0 0 16 16"
      width="12"
      height="12"
      aria-hidden="true"
    >
      <path d="M4 7V5a4 4 0 0 1 8 0v2h1v8H3V7h1zm2 0h4V5a2 2 0 0 0-4 0v2z" fill="currentColor" />
    </svg>
  );
}

function HistoryDialog({ onClose }: { onClose: () => void }) {
  const history = useQuery({
    queryKey: ['permissions', 'history'],
    queryFn: () => api<PermissionChange[]>('/api/admin/permissions/history'),
  });
  const value = (v: string | null) =>
    v && t(`admin.permissions.value.${v}`) !== `admin.permissions.value.${v}`
      ? pillText(v as PermissionValue)
      : (v ?? '—');
  const columns: Column<PermissionChange>[] = [
    { key: 'at', header: t('admin.settings.when'), render: (c) => formatDayTime(c.at) },
    { key: 'who', header: t('admin.settings.who'), render: (c) => c.who },
    {
      key: 'what',
      header: t('admin.permissions.change'),
      render: (c) => [c.what, c.role, c.key].filter(Boolean).join(' · '),
    },
    { key: 'old', header: t('admin.settings.old'), render: (c) => value(c.old) },
    { key: 'new', header: t('admin.settings.new'), render: (c) => value(c.new) },
  ];
  return (
    <Modal title={t('admin.permissions.historyTitle')} onClose={onClose} width={860}>
      {history.data && history.data.length === 0 && <p>{t('admin.settings.noHistory')}</p>}
      {history.data && history.data.length > 0 && (
        <Table
          caption={t('admin.permissions.historyTitle')}
          columns={columns}
          rows={history.data}
          rowKey={(c) => `${c.at}-${c.role}-${c.key}`}
        />
      )}
      <div className="permissions-dialog__buttons">
        <Button onClick={onClose}>{t('common.close')}</Button>
      </div>
    </Modal>
  );
}
