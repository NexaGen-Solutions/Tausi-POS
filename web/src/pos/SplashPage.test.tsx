import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { saveDevice } from '../shared/device/device';
import { SplashPage } from './SplashPage';

function renderSplash(open: (url: string) => void) {
  return render(
    <MemoryRouter initialEntries={['/splash']}>
      <Routes>
        <Route path="/splash" element={<SplashPage open={open} />} />
        <Route path="/connect" element={<p>Setup screen</p>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('SplashPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('shows the logo and the server, then opens the POS once the server answers', async () => {
    await saveDevice({ serverUrl: 'http://pos-server:5080', deviceToken: 'token' });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}', { status: 200 })));
    const open = vi.fn();
    renderSplash(open);

    expect(screen.getByRole('img', { name: 'Tausi POS' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Connecting to pos-server:5080…');
    await waitFor(() => expect(open).toHaveBeenCalledWith('http://pos-server:5080/pos'));
  });

  it('keeps trying while the server is down', async () => {
    await saveDevice({ serverUrl: 'http://pos-server:5080', deviceToken: 'token' });
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('offline')));
    const open = vi.fn();
    renderSplash(open);

    expect(
      await screen.findByText('Cannot reach pos-server:5080. Trying again…'),
    ).toBeInTheDocument();
    expect(open).not.toHaveBeenCalled();
  });

  it('sends an unpaired terminal to the setup screen', async () => {
    await saveDevice({});
    renderSplash(vi.fn());
    expect(screen.getByText('Setup screen')).toBeInTheDocument();
  });
});
