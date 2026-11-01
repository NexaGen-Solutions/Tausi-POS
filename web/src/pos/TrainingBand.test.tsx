import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { saveDevice } from '../shared/device/device';
import { json, renderWithProviders } from '../test/render';
import { TrainingBand } from './TrainingBand';

describe('TrainingBand', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
    document.documentElement.style.removeProperty('--training-band');
  });

  const device = (training: boolean) =>
    vi.fn(async () =>
      json(200, {
        terminalId: 't1',
        terminalName: 'Counter A',
        businessName: 'Demo Bar',
        logoFileId: null,
        workPeriod: null,
        training,
      }),
    );

  it('shows across the top of a training terminal and makes room for itself', async () => {
    await saveDevice({ serverUrl: null, deviceToken: 'token' });
    vi.stubGlobal('fetch', device(true));
    renderWithProviders(<TrainingBand />);

    expect(await screen.findByText('TRAINING · not real sales')).toBeInTheDocument();
    expect(document.documentElement.style.getPropertyValue('--training-band')).toBe('32px');
  });

  it('is not there on a real terminal', async () => {
    await saveDevice({ serverUrl: null, deviceToken: 'token' });
    const fetch = device(false);
    vi.stubGlobal('fetch', fetch);
    renderWithProviders(<TrainingBand />);

    await vi.waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(screen.queryByText('TRAINING · not real sales')).not.toBeInTheDocument();
    expect(document.documentElement.style.getPropertyValue('--training-band')).toBe('');
  });
});
