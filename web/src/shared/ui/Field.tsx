import { useId, type InputHTMLAttributes } from 'react';

export interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  hint?: string;
  error?: string | null;
}

/** A labelled input, with an optional hint and error message. */
export function Field({ label, hint, error, id, className, ...input }: FieldProps) {
  const generated = useId();
  const inputId = id ?? generated;
  const hintId = `${inputId}-hint`;
  const errorId = `${inputId}-error`;
  const describedBy = [hint ? hintId : '', error ? errorId : ''].join(' ').trim() || undefined;

  return (
    <div className={`nx-field${className ? ` ${className}` : ''}`}>
      <label className="nx-field__label" htmlFor={inputId}>
        {label}
      </label>
      <input
        id={inputId}
        className="nx-field__input"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        {...input}
      />
      {hint && (
        <span id={hintId} className="nx-field__hint">
          {hint}
        </span>
      )}
      {error && (
        <span id={errorId} className="nx-field__error">
          {error}
        </span>
      )}
    </div>
  );
}
