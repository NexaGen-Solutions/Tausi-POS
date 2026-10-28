import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { json, renderWithProviders } from '../../test/render';
import { DiskBanner } from './DiskBanner';
import { ServerStateOverlay } from './ServerStateOverlay';
import { serverMode, setServerMode } from './serverState';

/** Task 16, Part C: the database away for a while, and a nearly full disk. */
describe('server failures on screen', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    setServerMode('normal');
  });

  it('shows "Server busy, retrying" while the database is away, and clears when it answers', async () => {
    let databaseUp = false;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const path = new URL(String(input), 'http://localhost').pathname;
        if (path === '/api/health')
          return json(200, { database: databaseUp ? 'ok' : 'unreachable' });
        return databaseUp
          ? json(200, [])
          : json(503, {
              status: 503,
              title: 'Server busy',
              code: 'server_busy',
              detail: 'Retrying…',
            });
      }),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    render(<ServerStateOverlay />);

    await expect(api('/api/pos/tables')).rejects.toThrow();
    expect(serverMode()).toBe('busy');
    expect(
      await screen.findByRole('alertdialog', { name: 'Server busy, retrying…' }),
    ).toBeInTheDocument();

    databaseUp = true;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3100);
    });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(serverMode()).toBe('normal');
  });

  it('warns on the admin site when the server has under 1 GB free', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(200, { diskFreeBytes: 300 * 1024 ** 2 })),
    );
    renderWithProviders(<DiskBanner />);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      "The server's disk is nearly full (300 MB free)",
    );
  });

  it('says nothing with enough space', async () => {
    const fetch = vi.fn(async () => json(200, { diskFreeBytes: 50 * 1024 ** 3 }));
    vi.stubGlobal('fetch', fetch);
    renderWithProviders(<DiskBanner />);
    await waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
