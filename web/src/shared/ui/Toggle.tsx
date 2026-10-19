export interface ToggleProps {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

/** An on/off switch. A real button with role="switch", so it works with a keyboard too. */
export function Toggle({ label, checked, onChange, disabled }: ToggleProps) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      className="nx-toggle"
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span className="nx-toggle__track" aria-hidden="true">
        <span className="nx-toggle__thumb" />
      </span>
      <span>{label}</span>
    </button>
  );
}
