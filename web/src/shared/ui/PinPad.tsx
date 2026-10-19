import { useEffect, useRef, type KeyboardEvent } from 'react';
import { Keypad, PinDots } from './Keypad';

export const pinMaxLength = 6;
export const pinMinLength = 4;

export interface PinPadProps {
  value: string;
  onChange: (value: string) => void;
  onSubmit: (value: string) => void;
  /** Submits by itself when the PIN reaches this length (the longest PIN in use). */
  autoSubmitLength?: number | null;
  /** How many dots to show before anything is typed. */
  slots?: number;
  disabled?: boolean;
  /** Increase to shake the dots once (wrong PIN). */
  shakeKey?: number;
  enterLabel?: string;
  /**
   * Keep the keyboard on this pad: take focus back when it is lost to nothing (a tap on a blank
   * part of the screen, or the window being activated again). Use on full-screen PIN screens.
   */
  keepFocus?: boolean;
}

/**
 * PIN dots and keypad. Takes taps, and keys typed while the pad (or one of its buttons) has
 * focus: digits, Backspace, Escape (clear) and Enter. The digits are never shown.
 */
export function PinPad({
  value,
  onChange,
  onSubmit,
  autoSubmitLength,
  slots = pinMinLength,
  disabled = false,
  shakeKey = 0,
  enterLabel,
  keepFocus = false,
}: PinPadProps) {
  const pad = useRef<HTMLDivElement>(null);

  // Keep the keyboard working: focus the pad on show, and again when it is enabled.
  useEffect(() => {
    if (!disabled) pad.current?.focus();
  }, [disabled]);

  // Only takes focus back when nothing else has it, so other fields and dialogs keep theirs.
  useEffect(() => {
    if (!keepFocus) return;
    const reclaim = () => {
      const active = document.activeElement;
      if (!active || active === document.body) pad.current?.focus();
    };
    const later = () => window.setTimeout(reclaim, 0);
    document.addEventListener('pointerup', later);
    window.addEventListener('focus', later);
    return () => {
      document.removeEventListener('pointerup', later);
      window.removeEventListener('focus', later);
    };
  }, [keepFocus]);

  const add = (digit: string) => {
    if (disabled || value.length >= pinMaxLength) return;
    const next = value + digit;
    onChange(next);
    if (autoSubmitLength && next.length === autoSubmitLength) onSubmit(next);
  };

  const submit = () => {
    if (!disabled && value.length >= pinMinLength) onSubmit(value);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (disabled) return;
    if (/^\d$/.test(event.key)) add(event.key);
    else if (event.key === 'Backspace') onChange(value.slice(0, -1));
    else if (event.key === 'Escape') onChange('');
    else if (event.key === 'Enter') submit();
    else return;
    // Stop Enter from also "clicking" a focused keypad button.
    event.preventDefault();
  };

  return (
    <div ref={pad} className="nx-pinpad" tabIndex={-1} onKeyDown={onKeyDown}>
      <PinDots
        key={shakeKey}
        length={value.length}
        minDots={Math.max(slots, pinMinLength)}
        shake={shakeKey > 0}
      />
      <Keypad
        onDigit={add}
        onClear={() => onChange('')}
        onEnter={submit}
        enterLabel={enterLabel}
        disabled={disabled}
        enterDisabled={value.length < pinMinLength}
      />
    </div>
  );
}
