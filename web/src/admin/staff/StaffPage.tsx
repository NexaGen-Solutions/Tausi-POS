import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { api } from '../../shared/api/client';
import type { Me } from '../../shared/api/types';
import { formatDayTime } from '../../shared/format/dates';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, Field, Modal, SelectField, Table, type Column } from '../../shared/ui';
import { serverErrors } from './errors';
import { OneTimePin } from './OneTimePin';
import { PinForm } from './PinForm';
import { StaffForm } from './StaffForm';
import type { StaffList, StaffMember, StaffSaved, StatusFilter } from './types';
import './staff.css';

type Panel =
  | { kind: 'add' }
  | { kind: 'edit'; member: StaffMember }
  | { kind: 'pin'; member: StaffMember; reactivate: boolean };

/**
 * Wireframe 15: the staff list with a right-side panel to add, edit, reset PIN and reactivate.
 * The panel opens when needed, so the list (with its extra columns) has the full width otherwise.
 */
export function StaffPage() {
  const queries = useQueryClient();
  const staff = useQuery({
    queryKey: ['staff'],
    queryFn: () => api<StaffList>('/api/admin/staff'),
  });
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me') });

  const [panel, setPanel] = useState<Panel | null>(null);
  const [role, setRole] = useState('');
  const [status, setStatus] = useState<StatusFilter>('active');
  const [search, setSearch] = useState('');
  const [shown, setShown] = useState<{ name: string; pin: string } | null>(null);
  const [stale, setStale] = useState(false);
  const [confirming, setConfirming] = useState<StaffMember | null>(null);
  const [confirmError, setConfirmError] = useState<string | null>(null);

  const members = useMemo(() => staff.data?.members ?? [], [staff.data]);
  const roleNames = useMemo(
    () => [...new Set(members.map((m) => m.roleName))].sort((a, b) => a.localeCompare(b)),
    [members],
  );
  const visible = useMemo(() => {
    const term = search.trim().toLowerCase();
    return members
      .filter((m) => (status === 'all' ? true : status === 'active' ? m.active : !m.active))
      .filter((m) => !role || m.roleName === role)
      .filter((m) => !term || m.name.toLowerCase().includes(term))
      .sort((a, b) => Number(b.active) - Number(a.active) || a.name.localeCompare(b.name));
  }, [members, role, search, status]);

  const refresh = () => queries.invalidateQueries({ queryKey: ['staff'] });

  const saved = (result: StaffSaved) => {
    void refresh();
    setStale(false);
    if (result.temporaryPin) setShown({ name: result.member.name, pin: result.temporaryPin });
    setPanel(null);
  };

  const reload = async () => {
    setStale(false);
    setPanel(null);
    await refresh();
  };

  const deactivate = async (member: StaffMember) => {
    setConfirmError(null);
    try {
      await api(`/api/admin/staff/${member.id}/deactivate`, {
        method: 'POST',
        body: { rowVersion: member.rowVersion },
      });
      setConfirming(null);
      void refresh();
    } catch (error) {
      const found = serverErrors(error);
      if (found.stale) {
        setConfirming(null);
        setStale(true);
      } else setConfirmError(found.form ?? found.name ?? t('common.somethingWrong'));
    }
  };

  const myId = me.data?.user.id;
  const columns: Column<StaffMember>[] = [
    {
      key: 'name',
      header: t('admin.staff.colName'),
      render: (m) => (
        <>
          <span className="staff__name">{m.name}</span>
          {m.mustChangePin && <span className="staff-tag">{t('admin.staff.pinResetTag')}</span>}
        </>
      ),
    },
    { key: 'role', header: t('admin.staff.colRole'), render: (m) => m.roleName },
    {
      key: 'status',
      header: t('admin.staff.colStatus'),
      render: (m) => (m.active ? t('admin.staff.statusActive') : t('admin.staff.statusInactive')),
    },
    {
      key: 'last',
      header: t('admin.staff.colLastLogin'),
      render: (m) =>
        m.lastLogin ? (
          <>
            <span className="staff__login">{formatDayTime(m.lastLogin.at)}</span>
            {m.lastLogin.terminalName && (
              <span className="staff__login-where">{m.lastLogin.terminalName}</span>
            )}
          </>
        ) : (
          t('admin.staff.never')
        ),
    },
    {
      key: 'terminals',
      header: t('admin.staff.colTerminals'),
      render: (m) =>
        m.terminalsLast7Days.length ? m.terminalsLast7Days.join(', ') : t('admin.staff.none'),
    },
    {
      key: 'actions',
      header: t('admin.staff.colActions'),
      render: (m) => (
        <div className="staff__actions">
          <Button
            variant="link"
            aria-label={`${t('admin.staff.edit')} ${m.name}`}
            onClick={() => setPanel({ kind: 'edit', member: m })}
          >
            {t('admin.staff.edit')}
          </Button>
          {m.active ? (
            <>
              <Button
                variant="link"
                aria-label={`${t('admin.staff.resetPin')} ${m.name}`}
                onClick={() => setPanel({ kind: 'pin', member: m, reactivate: false })}
              >
                {t('admin.staff.resetPin')}
              </Button>
              {m.id !== myId && (
                <Button
                  variant="link"
                  aria-label={`${t('admin.staff.deactivate')} ${m.name}`}
                  onClick={() => setConfirming(m)}
                >
                  {t('admin.staff.deactivate')}
                </Button>
              )}
            </>
          ) : (
            <Button
              variant="link"
              aria-label={`${t('admin.staff.reactivate')} ${m.name}`}
              onClick={() => setPanel({ kind: 'pin', member: m, reactivate: true })}
            >
              {t('admin.staff.reactivate')}
            </Button>
          )}
        </div>
      ),
    },
  ];

  const roles = staff.data?.assignableRoles ?? [];
  const panelTitle = !panel
    ? ''
    : panel.kind === 'add'
      ? t('admin.staff.addStaff')
      : panel.kind === 'edit'
        ? t('admin.staff.editStaff', { name: panel.member.name })
        : panel.reactivate
          ? t('admin.staff.reactivateTitle', { name: panel.member.name })
          : t('admin.staff.resetPinTitle', { name: panel.member.name });

  return (
    <main className={`staff${panel ? ' staff--panel' : ''}`}>
      <div className="staff__main">
        <div className="staff__header">
          <h1 className="admin__title">{t('admin.staff.title')}</h1>
          <Button variant="primary" onClick={() => setPanel({ kind: 'add' })}>
            {t('admin.staff.addStaff')}
          </Button>
        </div>
        {stale && (
          <Banner action={{ label: t('common.reload'), onClick: () => void reload() }}>
            {t('admin.staff.stale')}
          </Banner>
        )}
        <div className="staff__filters">
          <SelectField
            label={t('admin.staff.roleFilter')}
            value={role}
            onChange={(e) => setRole(e.target.value)}
            options={[
              { value: '', label: t('admin.staff.allRoles') },
              ...roleNames.map((r) => ({ value: r, label: r })),
            ]}
          />
          <SelectField
            label={t('admin.staff.statusFilter')}
            value={status}
            onChange={(e) => setStatus(e.target.value as StatusFilter)}
            options={[
              { value: 'active', label: t('admin.staff.statusActive') },
              { value: 'inactive', label: t('admin.staff.statusInactive') },
              { value: 'all', label: t('admin.staff.statusAll') },
            ]}
          />
          <Field
            className="staff__search"
            label={t('admin.staff.searchLabel')}
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        <Table
          caption={t('admin.staff.title')}
          columns={columns}
          rows={visible}
          rowKey={(m) => m.id}
          rowClassName={(m) => (m.active ? undefined : 'staff-row--inactive')}
          empty={staff.isSuccess ? t('admin.staff.empty') : t('common.loading')}
        />
      </div>

      {panel && (
        <aside className="staff-panel" aria-label={panelTitle}>
          <h2 className="staff-panel__title">{panelTitle}</h2>
          {panel.kind === 'add' && (
            <>
              <StaffForm
                roles={roles}
                onSaved={saved}
                onStale={() => setStale(true)}
                onCancel={() => setPanel(null)}
              />
              {roles.every((r) => !r.canUseAdminSite) && (
                <p className="staff-panel__note">{t('admin.staff.supervisorHint')}</p>
              )}
            </>
          )}
          {panel.kind === 'edit' && (
            <StaffForm
              key={panel.member.id}
              roles={roles}
              member={panel.member}
              roleLocked={panel.member.id === myId}
              onSaved={saved}
              onStale={() => setStale(true)}
              onCancel={() => setPanel(null)}
            />
          )}
          {panel.kind === 'pin' && (
            <PinForm
              key={`${panel.member.id}-${panel.reactivate}`}
              member={panel.member}
              reactivate={panel.reactivate}
              onSaved={saved}
              onStale={() => setStale(true)}
              onCancel={() => setPanel(null)}
            />
          )}
        </aside>
      )}

      {confirming && (
        <Modal
          title={t('admin.staff.deactivateTitle', { name: confirming.name })}
          onClose={() => setConfirming(null)}
        >
          <div className="staff-pin-once">
            <p>{t('admin.staff.deactivateHelp', { name: confirming.name })}</p>
            {confirmError && (
              <p className="staff-panel__error" role="alert">
                {confirmError}
              </p>
            )}
            <Button variant="danger" size="lg" block onClick={() => void deactivate(confirming)}>
              {t('admin.staff.deactivate')}
            </Button>
            <Button block onClick={() => setConfirming(null)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}

      {shown && <OneTimePin name={shown.name} pin={shown.pin} onDone={() => setShown(null)} />}
    </main>
  );
}
