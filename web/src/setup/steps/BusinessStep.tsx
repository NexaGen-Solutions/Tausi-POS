import { useState } from 'react';
import { ApiError, api, apiUrl } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, SelectField } from '../../shared/ui';
import type { Settings } from '../../admin/settings/types';
import { NextButton, type StepProps } from '../SetupWizard';

const types = ['Bar', 'Restaurant', 'BarAndRestaurant'];
const currencies = ['KES', 'UGX', 'TZS', 'RWF', 'USD'];

/** Every time zone this browser knows, for the searchable list. */
function zones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  return (
    intl.supportedValuesOf?.('timeZone') ?? [
      'Africa/Nairobi',
      'Africa/Kampala',
      'Africa/Dar_es_Salaam',
      'Africa/Kigali',
      'UTC',
    ]
  );
}

/** Step 1, Your business (wireframe 21): name, type, address, phone, VAT, currency, logo and the time zone. */
export function BusinessStep({ status, onNext }: StepProps) {
  const [form, setForm] = useState({
    name: status.businessName ?? '',
    type: status.businessType ?? 'Bar',
    address: '',
    phone: '',
    vatRate: '16',
    currency: 'KES',
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'Africa/Nairobi',
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [logo, setLogo] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (key: keyof typeof form, value: string) => setForm((f) => ({ ...f, [key]: value }));

  const save = async () => {
    setBusy(true);
    setErrors({});
    setError(null);
    try {
      await api('/api/setup/business', {
        method: 'PUT',
        body: { ...form, vatRate: Number(form.vatRate) },
      });
      onNext();
    } catch (e) {
      if (e instanceof ApiError && e.problem.fieldErrors) {
        const f = e.problem.fieldErrors;
        setErrors({
          name: f['business.name'] ?? '',
          vatRate: f['business.vatRate'] ?? '',
          currency: f['business.currency'] ?? '',
          timeZone: f['business.timeZone'] ?? '',
          type: f.type ?? '',
        });
      } else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const upload = async (file: File | undefined) => {
    if (!file) return;
    if (!['image/png', 'image/jpeg'].includes(file.type))
      return setError(t('admin.settings.logoType'));
    const data = new FormData();
    data.append('file', file);
    try {
      const saved = await api<Settings>('/api/setup/logo', { method: 'POST', body: data });
      setLogo(saved.logoFileId);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };

  return (
    <form
      className="wizard__form"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <div className="wizard__grid">
        <Field
          label={t('wizard.business.name')}
          placeholder={t('wizard.business.namePlaceholder')}
          value={form.name}
          onChange={(e) => set('name', e.target.value)}
          error={errors.name || null}
          maxLength={80}
        />
        <SelectField
          label={t('wizard.business.type')}
          value={form.type}
          onChange={(e) => set('type', e.target.value)}
          error={errors.type || null}
          options={types.map((v) => ({ value: v, label: t(`wizard.business.types.${v}`) }))}
        />
        <Field
          label={t('wizard.business.address')}
          value={form.address}
          onChange={(e) => set('address', e.target.value)}
          maxLength={200}
        />
        <Field
          label={t('wizard.business.phone')}
          value={form.phone}
          onChange={(e) => set('phone', e.target.value)}
          maxLength={40}
        />
        <Field
          label={t('wizard.business.vat')}
          value={form.vatRate}
          inputMode="decimal"
          error={errors.vatRate || null}
          onChange={(e) => set('vatRate', e.target.value.replace(/[^\d.]/g, ''))}
        />
        <SelectField
          label={t('wizard.business.currency')}
          value={form.currency}
          onChange={(e) => set('currency', e.target.value)}
          options={currencies.map((c) => ({ value: c, label: c === 'KES' ? 'KES · KSh' : c }))}
        />
        <Field
          label={t('wizard.business.timeZone')}
          list="wizard-zones"
          value={form.timeZone}
          error={errors.timeZone || null}
          hint={t('wizard.business.timeZoneHint')}
          onChange={(e) => set('timeZone', e.target.value)}
        />
        <datalist id="wizard-zones">
          {zones().map((z) => (
            <option key={z} value={z} />
          ))}
        </datalist>
      </div>
      <div className="wizard__logo">
        <div className="wizard__logo-box">
          {logo ? (
            <img src={apiUrl(`/files/${logo}`)} alt={t('wizard.business.logo')} />
          ) : (
            <span>{t('wizard.business.noLogo')}</span>
          )}
        </div>
        <div>
          <label className="nx-btn nx-btn--secondary nx-btn--md wizard__upload">
            {t('wizard.business.uploadLogo')}
            <input
              type="file"
              accept="image/png,image/jpeg"
              className="visually-hidden"
              onChange={(e) => void upload(e.target.files?.[0])}
            />
          </label>
          <p className="wizard__note">{t('wizard.business.logoHint')}</p>
        </div>
      </div>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <NextButton
        next="admin"
        busy={busy}
        disabled={form.name.trim() === ''}
        onClick={() => void save()}
      />
      <Button type="submit" className="visually-hidden" tabIndex={-1}>
        {t('common.save')}
      </Button>
    </form>
  );
}
