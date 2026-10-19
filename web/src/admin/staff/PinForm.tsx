import { useState } from 'react';
import { api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field } from '../../shared/ui';
import { serverErrors, validatePin, type FieldErrors } from './errors';
import type { StaffMember, StaffSaved } from './types';

export interface PinFormProps {
  member: StaffMember;
  /** Reactivate an inactive person (a new PIN is required) instead of resetting the PIN. */
  reactivate: boolean;
  onSaved: (saved: StaffSaved) => void;
  onStale: () => void;
  onCancel: () => void;
}

/** Reset PIN or Reactivate: type a temporary PIN, or press Generate for a random valid one. */
export function PinForm({ member, reactivate, onSaved, onStale, onCancel }: PinFormProps) {
  const [pin, setPin] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);

  const submit = async (generate: boolean) => {
    if (!generate) {
      const pinError = validatePin(pin);
      if (pinError) {
        setErrors({ pin: pinError });
        return;
      }
    }
    setBusy(true);
    setErrors({});
    try {
      const saved = await api<StaffSaved>(
        `/api/admin/staff/${member.id}/${reactivate ? 'reactivate' : 'reset-pin'}`,
        {
          method: 'POST',
          body: { newPin: generate ? null : pin, generate, rowVersion: member.rowVersion },
        },
      );
      setPin('');
      onSaved(saved);
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
        void submit(false);
      }}
    >
      <p className="staff-panel__note">
        {reactivate
          ? t('admin.staff.reactivateHelp', { name: member.name })
          : t('admin.staff.resetPinHelp', { name: member.name })}
      </p>
      <Field
        label={t('admin.staff.newPin')}
        type="password"
        inputMode="numeric"
        autoComplete="off"
        maxLength={6}
        value={pin}
        onChange={(e) => setPin(e.target.value.replace(/\D/g, ''))}
        error={errors.pin}
        className="staff-panel__pin"
      />
      <Button onClick={() => void submit(true)} disabled={busy}>
        {t('admin.staff.generate')}
      </Button>
      {errors.form && (
        <p className="staff-panel__error" role="alert">
          {errors.form}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {reactivate ? t('admin.staff.reactivate') : t('admin.staff.setPin')}
      </Button>
      <Button block onClick={onCancel}>
        {t('common.cancel')}
      </Button>
    </form>
  );
}
