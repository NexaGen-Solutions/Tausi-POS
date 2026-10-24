import { useId } from 'react';
import { t } from '../../shared/i18n/i18n';
import { Field, SelectField } from '../../shared/ui';
import { timeZones, type Edit } from './form';
import type { SettingEntry } from './types';

interface Props {
  entry: SettingEntry;
  edit: Edit;
  changed: boolean;
  error?: string;
  onChange: (edit: Edit) => void;
  /** Words in the label, e.g. {minutes} in "Log out after {minutes} minutes idle". */
  labelValues?: Record<string, string | number>;
}

/** A choice's words, e.g. "Nearest shilling" for NearestOne; the value itself if there are none. */
const choice = (value: string) => {
  const key = `admin.settings.choice.${value}`;
  return t(key) === key ? value : t(key);
};

const fill = (text: string, values?: Record<string, string | number>) =>
  values ? text.replace(/\{(\w+)\}/g, (m, name: string) => String(values[name] ?? m)) : text;

/** One setting as the right control: a switch, a number with its range, a choice, text or the notes list. */
export function SettingField({ entry, edit, changed, error, onChange, labelValues }: Props) {
  const id = useId();
  const label = fill(entry.label, labelValues);
  const className = changed ? 'settings-field settings-field--changed' : 'settings-field';

  if (entry.type === 'Bool' || entry.key === 'print.mode') {
    const on = entry.type === 'Bool' ? edit === true : edit === 'OneButton';
    return (
      <div className={`settings-switch ${className}`}>
        <div className="settings-switch__text">
          <span id={`${id}-label`} className="settings-switch__label">
            {label}
          </span>
          <span id={`${id}-help`} className="settings-switch__help">
            {entry.help}
          </span>
          {error && <span className="nx-field__error">{error}</span>}
        </div>
        <button
          type="button"
          role="switch"
          aria-checked={on}
          aria-labelledby={`${id}-label`}
          aria-describedby={`${id}-help`}
          className="nx-toggle settings-switch__toggle"
          onClick={() => onChange(entry.type === 'Bool' ? !on : on ? 'SendAndBill' : 'OneButton')}
        >
          <span className="nx-toggle__track" aria-hidden="true">
            <span className="nx-toggle__thumb" />
          </span>
        </button>
      </div>
    );
  }

  const text = typeof edit === 'string' ? edit : '';
  if (entry.type === 'Enum') {
    return (
      <SelectField
        className={className}
        label={label}
        value={text}
        error={error}
        onChange={(e) => onChange(e.target.value)}
        options={(entry.allowed ?? []).map((value) => ({ value, label: choice(value) }))}
      />
    );
  }

  if (entry.key === 'business.receiptFooter') {
    return (
      <div className={`nx-field ${className}`}>
        <label className="nx-field__label" htmlFor={id}>
          {label}
        </label>
        <textarea
          id={id}
          className="nx-field__input settings-textarea"
          value={text}
          rows={3}
          maxLength={entry.maxLength || undefined}
          placeholder={entry.help}
          aria-invalid={error ? true : undefined}
          onChange={(e) => onChange(e.target.value)}
        />
        {error && <span className="nx-field__error">{error}</span>}
      </div>
    );
  }

  const numeric = entry.type === 'Int' || entry.type === 'Decimal';
  const hint =
    entry.type === 'List'
      ? t('admin.settings.listHint')
      : numeric && entry.min !== null && entry.max !== null
        ? t('admin.settings.range', { min: entry.min, max: entry.max.toLocaleString('en-KE') })
        : entry.key === 'business.timeZone'
          ? entry.help
          : undefined;
  return (
    <>
      <Field
        className={className}
        label={label}
        value={text}
        hint={hint}
        error={error}
        inputMode={numeric ? 'decimal' : undefined}
        maxLength={entry.maxLength || undefined}
        list={entry.key === 'business.timeZone' ? `${id}-zones` : undefined}
        autoComplete="off"
        onChange={(e) => onChange(e.target.value)}
      />
      {entry.key === 'business.timeZone' && (
        <datalist id={`${id}-zones`}>
          {timeZones(text).map((zone) => (
            <option key={zone} value={zone} />
          ))}
        </datalist>
      )}
    </>
  );
}
