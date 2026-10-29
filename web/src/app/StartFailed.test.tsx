import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { StartFailed } from './StartFailed';

describe('StartFailed', () => {
  it('names what went wrong instead of leaving the screen blank', () => {
    render(<StartFailed error={new Error('get_device_config not allowed')} />);
    expect(screen.getByRole('alert')).toHaveTextContent("Tausi POS couldn't start");
    expect(screen.getByText('get_device_config not allowed')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });
});
