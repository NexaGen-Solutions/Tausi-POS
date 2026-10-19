import { ApiError } from '../../shared/api/client';
import { isTooSimplePin, isWellFormedPin } from '../../shared/format/pin';
import { t } from '../../shared/i18n/i18n';

export type FieldErrors = Partial<Record<'name' | 'role' | 'pin' | 'password' | 'form', string>>;

/** Puts a server error next to the field it is about. */
export function serverErrors(error: unknown): FieldErrors & { stale?: boolean } {
  if (!(error instanceof ApiError)) return { form: t('common.somethingWrong') };
  const code = error.problem.code ?? '';
  const message = error.problem.detail ?? error.message;
  if (error.status === 409) return { stale: true, form: t('admin.staff.stale') };
  if (code.startsWith('pin_')) return { pin: message };
  if (code.startsWith('name_')) return { name: message };
  if (code.startsWith('password_')) return { password: message };
  if (code === 'role_not_assignable') return { role: message };
  return { form: message };
}

export function validatePin(pin: string): string | undefined {
  if (!isWellFormedPin(pin)) return t('admin.staff.errors.pinInvalid');
  if (isTooSimplePin(pin)) return t('admin.staff.errors.pinTooSimple');
  return undefined;
}
