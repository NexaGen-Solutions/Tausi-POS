import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField } from '../../shared/ui';
import type { PermissionsGrid, RoleInfo } from './types';

interface Props {
  grid: PermissionsGrid;
  onDone: (grid: PermissionsGrid | null) => void;
}

function useSave(onDone: (grid: PermissionsGrid | null) => void) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (request: () => Promise<PermissionsGrid>) => {
    setBusy(true);
    setError(null);
    try {
      onDone(await request());
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  return { error, busy, run };
}

/** "+ Add role": a name, and the role whose permissions it starts with. */
export function AddRoleDialog({ grid, onDone }: Props) {
  const [name, setName] = useState('');
  const [from, setFrom] = useState('');
  const { error, busy, run } = useSave(onDone);
  return (
    <Modal title={t('admin.permissions.addRole')} onClose={() => onDone(null)} width={460}>
      <form
        className="permissions-dialog"
        onSubmit={(e) => {
          e.preventDefault();
          void run(() =>
            api<PermissionsGrid>('/api/admin/roles', {
              method: 'POST',
              body: { name, copyFromRoleId: from || null },
            }),
          );
        }}
      >
        <Field
          label={t('admin.permissions.roleName')}
          value={name}
          maxLength={40}
          error={error}
          onChange={(e) => setName(e.target.value)}
          autoFocus
        />
        <SelectField
          label={t('admin.permissions.copyFrom')}
          value={from}
          onChange={(e) => setFrom(e.target.value)}
          options={[
            { value: '', label: t('admin.permissions.copyNone') },
            ...grid.roles.map((r) => ({ value: r.id, label: r.name })),
          ]}
        />
        <div className="permissions-dialog__buttons">
          <Button onClick={() => onDone(null)}>{t('common.cancel')}</Button>
          <Button type="submit" variant="primary" disabled={busy || !name.trim()}>
            {t('admin.permissions.addRoleSave')}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

export function RenameRoleDialog({ role, onDone }: { role: RoleInfo; onDone: Props['onDone'] }) {
  const [name, setName] = useState(role.name);
  const { error, busy, run } = useSave(onDone);
  return (
    <Modal
      title={t('admin.permissions.renameTitle', { name: role.name })}
      onClose={() => onDone(null)}
      width={460}
    >
      <form
        className="permissions-dialog"
        onSubmit={(e) => {
          e.preventDefault();
          void run(() =>
            api<PermissionsGrid>(`/api/admin/roles/${role.id}`, {
              method: 'PUT',
              body: { name, rowVersion: role.rowVersion },
            }),
          );
        }}
      >
        <Field
          label={t('admin.permissions.roleName')}
          value={name}
          maxLength={40}
          error={error}
          onChange={(e) => setName(e.target.value)}
          autoFocus
        />
        <div className="permissions-dialog__buttons">
          <Button onClick={() => onDone(null)}>{t('common.cancel')}</Button>
          <Button type="submit" variant="primary" disabled={busy || !name.trim()}>
            {t('common.save')}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/** Only a role nobody has; otherwise it says who has it. */
export function DeleteRoleDialog({ role, onDone }: { role: RoleInfo; onDone: Props['onDone'] }) {
  const { error, busy, run } = useSave(onDone);
  const blocked = role.users.length > 0;
  return (
    <Modal
      title={t('admin.permissions.deleteTitle', { name: role.name })}
      onClose={() => onDone(null)}
      width={460}
    >
      <p role={blocked || error ? 'alert' : undefined}>
        {blocked
          ? t('admin.permissions.deleteBlocked', { users: role.users.join(', ') })
          : (error ?? t('admin.permissions.deleteText'))}
      </p>
      <div className="permissions-dialog__buttons">
        <Button onClick={() => onDone(null)}>
          {blocked ? t('common.close') : t('common.cancel')}
        </Button>
        {!blocked && (
          <Button
            variant="danger"
            disabled={busy}
            onClick={() =>
              void run(() =>
                api<PermissionsGrid>(`/api/admin/roles/${role.id}`, { method: 'DELETE' }),
              )
            }
          >
            {t('admin.permissions.delete')}
          </Button>
        )}
      </div>
    </Modal>
  );
}
