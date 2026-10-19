import { t } from '../i18n/i18n';

export interface KeypadProps {
  onDigit: (digit: string) => void;
  onClear: () => void;
  onEnter: () => void;
  enterLabel?: string;
  enterDisabled?: boolean;
  /** Disables every key, e.g. while locked or offline. */
  disabled?: boolean;
  /** "large" is the login pad (wireframe 1); "compact" fits inside dialogs (wireframe 5). */
  variant?: 'large' | 'compact';
}

const digits = ['1', '2', '3', '4', '5', '6', '7', '8', '9'];

/** The PIN pad: 1-9, then Clear, 0, Enter. */
export function Keypad({
  onDigit,
  onClear,
  onEnter,
  enterLabel = t('common.enter'),
  enterDisabled = false,
  disabled = false,
  variant = 'large',
}: KeypadProps) {
  return (
    <div className={`nx-keypad nx-keypad--${variant}`} role="group" aria-label="Keypad">
      {digits.map((d) => (
        <button
          key={d}
          type="button"
          className="nx-keypad__key"
          onClick={() => onDigit(d)}
          disabled={disabled}
        >
          {d}
        </button>
      ))}
      <button
        type="button"
        className="nx-keypad__key nx-keypad__key--word"
        onClick={onClear}
        disabled={disabled}
      >
        {t('common.clear')}
      </button>
      <button
        type="button"
        className="nx-keypad__key"
        onClick={() => onDigit('0')}
        disabled={disabled}
      >
        0
      </button>
      <button
        type="button"
        className="nx-keypad__key nx-keypad__key--enter"
        onClick={onEnter}
        disabled={disabled || enterDisabled}
      >
        {enterLabel}
      </button>
    </div>
  );
}

export interface PinDotsProps {
  length: number;
  /** At least this many dots are shown; more appear for longer PINs. */
  minDots?: number;
  size?: 'large' | 'small';
  /** Shakes once (wrong PIN). Change the key to shake again. */
  shake?: boolean;
}

export function PinDots({ length, minDots = 4, size = 'large', shake = false }: PinDotsProps) {
  const count = Math.max(minDots, length);
  const classes = ['nx-pindots'];
  if (size === 'small') classes.push('nx-pindots--small');
  if (shake) classes.push('nx-pindots--shake');
  return (
    <ol
      className={classes.join(' ')}
      aria-label={t('pos.pinDigits', { count: length, max: count })}
    >
      {Array.from({ length: count }, (_, i) => (
        <li
          key={i}
          className={`nx-pindots__dot${i < length ? ' nx-pindots__dot--filled' : ''}`}
          aria-hidden="true"
        />
      ))}
    </ol>
  );
}
