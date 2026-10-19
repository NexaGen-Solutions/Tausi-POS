import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { Keypad, PinDots } from './Keypad';

describe('Keypad', () => {
  it('has 0-9, Clear and Enter as real buttons', async () => {
    const onDigit = vi.fn();
    const onClear = vi.fn();
    const onEnter = vi.fn();
    render(<Keypad onDigit={onDigit} onClear={onClear} onEnter={onEnter} />);

    for (const d of ['1', '5', '0']) await userEvent.click(screen.getByRole('button', { name: d }));
    await userEvent.click(screen.getByRole('button', { name: 'Clear' }));
    await userEvent.click(screen.getByRole('button', { name: 'Enter' }));

    expect(onDigit.mock.calls.map((c) => c[0])).toEqual(['1', '5', '0']);
    expect(onClear).toHaveBeenCalledOnce();
    expect(onEnter).toHaveBeenCalledOnce();
    expect(screen.getAllByRole('button')).toHaveLength(12);
  });

  it('can disable Enter', () => {
    render(<Keypad onDigit={vi.fn()} onClear={vi.fn()} onEnter={vi.fn()} enterDisabled />);
    expect(screen.getByRole('button', { name: 'Enter' })).toBeDisabled();
  });

  it('shows how many digits are entered', () => {
    render(<PinDots length={2} />);
    expect(screen.getByLabelText('2 of 4 digits entered')).toBeInTheDocument();
  });
});
