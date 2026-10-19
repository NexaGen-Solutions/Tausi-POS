import { useId, type SelectHTMLAttributes } from 'react';

export interface SelectFieldProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label: string;
  options: { value: string; label: string }[];
  error?: string | null;
}

/** A labelled drop-down. */
export function SelectField({ label, options, error, id, className, ...select }: SelectFieldProps) {
  const generated = useId();
  const selectId = id ?? generated;
  return (
    <div className={`nx-field${className ? ` ${className}` : ''}`}>
      <label className="nx-field__label" htmlFor={selectId}>
        {label}
      </label>
      <select
        id={selectId}
        className="nx-field__select"
        aria-invalid={error ? true : undefined}
        {...select}
      >
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
      {error && <span className="nx-field__error">{error}</span>}
    </div>
  );
}
