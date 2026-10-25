import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, SelectField } from '../../shared/ui';
import { savePdf } from '../files';
import { NextButton, type StepProps } from '../SetupWizard';
import type { NewStaffPin } from '../types';

/** A rough guide only; the rule is 10 characters or more. */
function strength(password: string): 'short' | 'weak' | 'good' | 'strong' {
  if (password.length < 10) return 'short';
  const kinds = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(password)).length;
  return password.length >= 14 && kinds >= 3 ? 'strong' : kinds >= 2 ? 'good' : 'weak';
}

/**
 * Step 2, First admin and PIN: the owner (admin-site name and password, a 4-digit PIN twice), then
 * an optional quick staff list. Their PINs show once, here, with the printable PIN sheet.
 */
export function AdminStep({ status, onNext }: StepProps) {
  const done = status.steps.find((s) => s.key === 'admin')?.status === 'Done';
  const [owner, setOwner] = useState({
    fullName: '',
    userName: '',
    password: '',
    pin: '',
    pinAgain: '',
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(done);
  const [people, setPeople] = useState([{ name: '', role: 'waiter' }]);
  const [created, setCreated] = useState<{ people: NewStaffPin[]; pinSheetPdf: string } | null>(
    null,
  );
  const [busy, setBusy] = useState(false);
  const set = (key: keyof typeof owner, value: string) => setOwner((o) => ({ ...o, [key]: value }));
  const hint = strength(owner.password);

  const saveOwner = async () => {
    setBusy(true);
    setErrors({});
    setError(null);
    try {
      await api('/api/setup/admin', { method: 'POST', body: owner });
      setSaved(true);
    } catch (e) {
      if (e instanceof ApiError && e.problem.fieldErrors) setErrors(e.problem.fieldErrors);
      else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const addStaff = async () => {
    setBusy(true);
    setErrors({});
    setError(null);
    try {
      const list = people.filter((p) => p.name.trim() !== '');
      setCreated(await api('/api/setup/staff', { method: 'POST', body: { people: list } }));
      setPeople([{ name: '', role: 'waiter' }]);
    } catch (e) {
      if (e instanceof ApiError && e.problem.fieldErrors) setErrors(e.problem.fieldErrors);
      else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  if (!saved)
    return (
      <form
        className="wizard__form"
        onSubmit={(e) => {
          e.preventDefault();
          void saveOwner();
        }}
      >
        <div className="wizard__grid">
          <Field
            label={t('wizard.admin.fullName')}
            value={owner.fullName}
            onChange={(e) => set('fullName', e.target.value)}
            error={errors.fullName}
          />
          <Field
            label={t('wizard.admin.userName')}
            value={owner.userName}
            autoComplete="username"
            onChange={(e) => set('userName', e.target.value)}
            error={errors.userName}
            hint={t('wizard.admin.userNameHint')}
          />
          <Field
            label={t('wizard.admin.password')}
            type="password"
            autoComplete="new-password"
            value={owner.password}
            onChange={(e) => set('password', e.target.value)}
            error={errors.password}
            hint={
              owner.password ? t(`wizard.admin.strength.${hint}`) : t('wizard.admin.passwordHint')
            }
          />
          <div />
          <Field
            label={t('wizard.admin.pin')}
            type="password"
            inputMode="numeric"
            maxLength={4}
            value={owner.pin}
            className="wizard__pin"
            onChange={(e) => set('pin', e.target.value.replace(/\D/g, ''))}
            error={errors.pin}
          />
          <Field
            label={t('wizard.admin.pinAgain')}
            type="password"
            inputMode="numeric"
            maxLength={4}
            value={owner.pinAgain}
            className="wizard__pin"
            onChange={(e) => set('pinAgain', e.target.value.replace(/\D/g, ''))}
            error={errors.pinAgain}
          />
        </div>
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
        <div className="wizard__next">
          <Button type="submit" variant="primary" size="lg" disabled={busy}>
            {t('wizard.admin.createOwner')}
          </Button>
        </div>
      </form>
    );

  return (
    <div className="wizard__form">
      <p className="wizard__ok" role="status">
        {t('wizard.admin.ownerReady')}
      </p>
      <section aria-labelledby="quick-staff">
        <h3 id="quick-staff" className="wizard__subtitle">
          {t('wizard.admin.staffTitle')}
        </h3>
        <p className="wizard__note">{t('wizard.admin.staffHelp')}</p>
        {people.map((p, i) => (
          <div key={i} className="wizard__row">
            <Field
              label={t('wizard.admin.personName', { n: i + 1 })}
              value={p.name}
              error={errors[`people.${i}.name`]}
              onChange={(e) =>
                setPeople((list) =>
                  list.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)),
                )
              }
            />
            <SelectField
              label={t('wizard.admin.role')}
              value={p.role}
              error={errors[`people.${i}.role`]}
              onChange={(e) =>
                setPeople((list) =>
                  list.map((x, j) => (j === i ? { ...x, role: e.target.value } : x)),
                )
              }
              options={['waiter', 'cashier', 'supervisor'].map((r) => ({
                value: r,
                label: t(`wizard.admin.roles.${r}`),
              }))}
            />
            {people.length > 1 && (
              <Button
                variant="link"
                onClick={() => setPeople((list) => list.filter((_, j) => j !== i))}
              >
                {t('wizard.admin.remove')}
              </Button>
            )}
          </div>
        ))}
        <div className="wizard__row">
          <Button onClick={() => setPeople((list) => [...list, { name: '', role: 'waiter' }])}>
            {t('wizard.admin.addPerson')}
          </Button>
          <Button
            variant="primary"
            disabled={busy || people.every((p) => p.name.trim() === '')}
            onClick={() => void addStaff()}
          >
            {t('wizard.admin.createStaff')}
          </Button>
        </div>
      </section>
      {created && <PinList people={created.people} pdf={created.pinSheetPdf} />}
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <NextButton next="devices" onClick={onNext} />
    </div>
  );
}

/** New people's first PINs: shown once (they are not kept readable anywhere), with the PIN sheet to print. */
export function PinList({ people, pdf }: { people: NewStaffPin[]; pdf: string | null }) {
  if (people.length === 0) return null;
  return (
    <section className="wizard__pins" aria-labelledby="first-pins">
      <h3 id="first-pins" className="wizard__subtitle">
        {t('wizard.pins.title')}
      </h3>
      <p className="wizard__warn">{t('wizard.pins.once')}</p>
      <table className="wizard__table">
        <thead>
          <tr>
            <th scope="col">{t('wizard.pins.name')}</th>
            <th scope="col">{t('wizard.pins.role')}</th>
            <th scope="col">{t('wizard.pins.pin')}</th>
          </tr>
        </thead>
        <tbody>
          {people.map((p) => (
            <tr key={p.id}>
              <td>{p.name}</td>
              <td>{p.role}</td>
              <td className="mono">{p.pin}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {pdf && (
        <Button onClick={() => savePdf(pdf, 'pin-sheet.pdf')}>{t('wizard.pins.print')}</Button>
      )}
    </section>
  );
}
