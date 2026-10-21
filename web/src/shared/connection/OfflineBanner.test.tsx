import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { OfflineBanner } from './OfflineBanner';
import { updateConnection } from './status';

describe('OfflineBanner', () => {
  afterEach(() => updateConnection({ apiReachable: true, hub: 'off' }));

  it('appears when the server cannot be reached and clears when it is back', () => {
    render(<OfflineBanner />);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    act(() => updateConnection({ apiReachable: false }));
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Not connected to the server. Reconnecting…',
    );

    act(() => updateConnection({ apiReachable: true }));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('appears while the live-update hub is reconnecting', () => {
    render(<OfflineBanner />);
    act(() => updateConnection({ hub: 'reconnecting' }));
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });
});
