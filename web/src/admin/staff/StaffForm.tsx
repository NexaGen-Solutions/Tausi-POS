import { useState } from 'react';
import { api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, SelectField } from '../../shared/ui';
import { serverErrors, validatePin, type FieldErrors } from './errors';
import type { StaffMember, StaffRole, StaffSaved } from './types';

export interface StaffFormProps {
  roles: StaffRole[];
  /** Editing this person; adding when absent. */
  member?: StaffMember;
  /** May the role be changed (not your own)? */
  roleLocked?: boolean;
  onSaved: (saved: StaffSaved) => void;
  onStale: () => void;
  onCancel?: () => void;
}

const minPassword = 10;

/** Add or edit a person: name, role, starting PIN (add only) and admin-site password. */
export function StaffForm({
  roles,
  member,
  roleLocked,
  onSaved,
  onStale,
  onCancel,
}: StaffFormProps) {
  const editing = !!member;
  const [name, setName] = useState(member?.name ?? '');
  // The list may arrive after the form: until a role is picked, use the first (Waiter).
  const [roleChoice, setRoleId] = useState<string | null>(member?.roleId ?? null);
  const roleId = roleChoice ?? roles[0]?.id ?? '';
  const [pin, setPin] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);

  // Your own role (or one you can't give) still shows, even if it is not in the list you may assign.
  const roleOptions = [...roles];
  if (member && !roles.some((r) => r.id === member.roleId))
    roleOptions.push({
      id: member.roleId,
      name: member.roleName,
      canUseAdminSite: member.hasAdminPassword,
    });
  const role = roleOptions.find((r) => r.id === roleId);
  const needsPassword = !!role?.canUseAdminSite && !(editing && member.hasAdminPassword);

  const validate = (): FieldErrors => {
    const found: FieldErrors = {};
    const trimmed = name.trim();
    if (trimmed.length < 2 || trimmed.length > 40) found.name = t('admin.staff.errors.nameInvalid');
    if (!role) found.role = t('admin.staff.errors.roleRequired');
    if (!editing) {
      const pinError = validatePin(pin);
      if (pinError) found.pin = pinError;
    }
    if (role?.canUseAdminSite) {
      if (!editing && !password) found.password = t('admin.staff.errors.passwordRequired');
      else if (password && password.length < minPassword)
        found.password = t('admin.staff.errors.passwordTooShort');
    }
    return found;
  };

  const save = async () => {
    const found = validate();
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    setBusy(true);
    try {
      if (editing) {
        const updated = await api<StaffMember>(`/api/admin/staff/${member.id}`, {
          method: 'PUT',
          body: {
            name: name.trim(),
            roleId,
            adminPassword: role?.canUseAdminSite && password ? password : null,
            rowVersion: member.rowVersion,
          },
        });
        onSaved({ member: updated, temporaryPin: null });
      } else {
        const saved = await api<StaffSaved>('/api/admin/staff', {
          method: 'POST',
          body: {
            name: name.trim(),
            roleId,
            startingPin: pin,
            adminPassword: role?.canUseAdminSite ? password : null,
          },
        });
        setName('');
        setPin('');
        setPassword('');
        setRoleId(null);
        onSaved(saved);
      }
    } catch (error) {
      const found = serverErrors(error);
      setErrors(found);
      if (found.stale) onStale();
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      className="staff-panel__form"
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      <Field
        label={t('admin.staff.name')}
        placeholder={t('admin.staff.namePlaceholder')}
        value={name}
        maxLength={40}
        onChange={(e) => setName(e.target.value)}
        error={errors.name}
        autoComplete="off"
      />
      <SelectField
        label={t('admin.staff.role')}
        value={roleId}
        onChange={(e) => setRoleId(e.target.value)}
        options={roleOptions.map((r) => ({ value: r.id, label: r.name }))}
        disabled={roleLocked}
        error={errors.role}
      />
      {!editing && (
        <Field
          label={t('admin.staff.startingPin')}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={6}
          value={pin}
          onChange={(e) => setPin(e.target.value.replace(/\D/g, ''))}
          hint={t('admin.staff.startingPinHint')}
          error={errors.pin}
          className="staff-panel__pin"
        />
      )}
      {role?.canUseAdminSite && (
        <Field
          label={
            editing
              ? member.hasAdminPassword
                ? t('admin.staff.resetPassword')
                : t('admin.staff.setPassword')
              : t('admin.staff.adminPassword')
          }
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          hint={
            editing && member.hasAdminPassword
              ? t('admin.staff.keepPassword')
              : t('admin.staff.adminPasswordHint')
          }
          error={errors.password}
          required={needsPassword && !editing}
        />
      )}
      {editing && member.adminUserName && (
        <p className="staff-panel__note">
          {t('admin.staff.signInName', { name: member.adminUserName })}
        </p>
      )}
      {errors.form && (
        <p className="staff-panel__error" role="alert">
          {errors.form}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {editing ? t('admin.staff.saveChanges') : t('admin.staff.saveStaff')}
      </Button>
      {onCancel && (
        <Button block onClick={onCancel}>
          {t('common.cancel')}
        </Button>
      )}
    </form>
  );
}
