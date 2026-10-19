import type { ButtonHTMLAttributes } from 'react';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'danger' | 'dark' | 'ghost-dark';
  /** md is 44 px high (any touch target); lg is 56 px (primary actions). */
  size?: 'md' | 'lg';
  block?: boolean;
}

export function Button({
  variant = 'secondary',
  size = 'md',
  block = false,
  className,
  type = 'button',
  ...rest
}: ButtonProps) {
  const classes = ['nx-btn', `nx-btn--${variant}`, `nx-btn--${size}`];
  if (block) classes.push('nx-btn--block');
  if (className) classes.push(className);
  return <button type={type} className={classes.join(' ')} {...rest} />;
}
