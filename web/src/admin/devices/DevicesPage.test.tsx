import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type { PrinterRow, PrintJobRow, TerminalRow } from './types';

const handlers = vi.hoisted(() => new Map<string, ((payload: unknown) => void)[]>());
vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn((name: string, handler: (payload: unknown) => void) => {
    handlers.set(name, [...(handlers.get(name) ?? []), handler]);
    return () =>
      handlers.set(
        name,
        (handlers.get(name) ?? []).filter((h) => h !== handler),
      );
  }),
}));
const emit = (name: string, payload: unknown) =>
  act(() => (handlers.get(name) ?? []).forEach((h) => h(payload)));

function terminal(name: string, extra: Partial<TerminalRow> = {}): TerminalRow {
  return {
    id: `t-${name}`,
    name,
    status: 'Offline',
    lastSeenAt: null,
    currentUser: null,
    appVersion: '0.1.0',
    priceListId: null,
    priceListName: null,
    dispensePrinterId: 'p-tp1',
    dispensePrinterName: 'Ticket Printer 1',
    dispenseHeader: null,
    effectiveDispenseHeader: name,
    receiptPrinterId: 'p-tp1',
    receiptPrinterName: 'Ticket Printer 1',
    active: true,
    paired: true,
    localPrinters: [],
    routingPreview: '',
    rowVersion: 1,
    ...extra,
  };
}

function printer(name: string, extra: Partial<PrinterRow> = {}): PrinterRow {
  return {
    id: `p-${name}`,
    name,
    connectionMode: 'File',
    address: '',
    owningTerminalId: null,
    owningTerminalName: null,
    connectionText: 'File (dev)',
    paperWidthMm: 80,
    cutAfterSlip: true,
    openDrawerOnReceipt: false,
    active: true,
    status: 'Unknown',
    lastError: null,
    lastPrintedAt: null,
    failedJobs: 0,
    usedBy: [],
    dispenseTemplate: null,
    rowVersion: 1,
    ...extra,
  };
}

const tp1 = printer('Ticket Printer 1', {
  id: 'p-tp1',
  usedBy: [{ terminal: 'Counter A', roles: ['Dispense', 'Receipt'] }],
});
const pillar = printer('Pillar 1', { id: 'p-pillar1' });

describe('Terminals and printers', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let terminals: TerminalRow[];
  let printers: PrinterRow[];

  function serve(extra: Parameters<typeof fakeServer>[0] = {}) {
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'me', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () => json(200, { name: 'Test Bar' }),
        'GET /api/admin/terminals': () => json(200, terminals),
        'GET /api/admin/printers': () => json(200, printers),
        'GET /api/admin/menu': () => json(200, { priceLists: [] }),
        ...extra,
      }),
    );
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    handlers.clear();
    terminals = [
      terminal('Counter A'),
      terminal('Pillar 1', { paired: false, status: 'NotPaired' }),
    ];
    printers = [tp1, pillar];
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  const terminalRow = (name: string) =>
    within(screen.getByRole('table', { name: 'Terminals' }))
      .getAllByRole('row')
      .find((row) => within(row).queryAllByRole('cell')[0]?.textContent === name)!;

  it('updates the terminal list live when a terminal connects', async () => {
    serve();
    renderRoutes(routes, '/admin/terminals');
    await screen.findByRole('button', { name: 'Edit Counter A' });
    expect(terminalRow('Counter A')).toHaveTextContent('Offline');

    terminals = [terminal('Counter A', { status: 'Online', currentUser: 'edna' }), terminals[1]];
    emit('terminal.status', { terminalId: 't-Counter A', online: true });

    await waitFor(() => expect(terminalRow('Counter A')).toHaveTextContent('Online'));
    expect(terminalRow('Counter A')).toHaveTextContent('edna');
  });

  it('shows the routing preview while editing a terminal', async () => {
    serve();
    renderRoutes(routes, '/admin/terminals');
    await screen.findByRole('button', { name: 'Edit Counter A' });
    await userEvent.click(
      within(terminalRow('Pillar 1')).getByRole('button', { name: 'Edit Pillar 1' }),
    );
    const panel = screen.getByRole('complementary');
    await userEvent.selectOptions(within(panel).getByLabelText('Dispense printer'), 'p-pillar1');
    expect(within(panel).getByTestId('routing-preview')).toHaveTextContent(
      'Print on Pillar 1: 1. Bill → Ticket Printer 1 · 2. Dispense slip "Pillar 1" → Pillar 1',
    );
    await userEvent.type(within(panel).getByLabelText('Dispense header'), 'Counter A');
    expect(within(panel).getByTestId('routing-preview')).toHaveTextContent(
      'Dispense slip "Counter A" → Pillar 1',
    );
  });

  describe('pairing', () => {
    async function openPair() {
      renderRoutes(routes, '/admin/terminals');
      await screen.findByRole('button', { name: 'Edit Counter A' });
      await userEvent.click(screen.getByRole('button', { name: 'Pair a terminal' }));
      return screen.getByRole('complementary');
    }

    it('pairs with the code and the chosen terminal', async () => {
      let sent: unknown;
      serve({
        'POST /api/admin/terminals/pair': ({ body }) => {
          sent = body;
          return json(200, { terminalId: 't-Pillar 1', terminalName: 'Pillar 1' });
        },
      });
      const panel = await openPair();
      await userEvent.type(within(panel).getByLabelText('6-digit code on the terminal'), '482 913');
      await userEvent.click(within(panel).getByRole('button', { name: 'Pair' }));
      expect(await within(panel).findByText(/Paired as Pillar 1\./)).toBeInTheDocument();
      expect(sent).toEqual({
        code: '482913',
        terminalId: 't-Pillar 1',
        newTerminalName: null,
        confirmReplace: false,
      });
    });

    it('says when the code is wrong', async () => {
      serve({
        'POST /api/admin/terminals/pair': () =>
          json(400, { status: 400, title: 'Code not found or expired.', code: 'pairing_code' }),
      });
      const panel = await openPair();
      await userEvent.type(within(panel).getByLabelText('6-digit code on the terminal'), '111111');
      await userEvent.click(within(panel).getByRole('button', { name: 'Pair' }));
      expect(await within(panel).findByRole('alert')).toHaveTextContent(
        'Code not found or expired',
      );
    });

    it('says when too many wrong codes were tried', async () => {
      serve({
        'POST /api/admin/terminals/pair': () =>
          json(429, { status: 429, title: 'Too many tries.' }),
      });
      const panel = await openPair();
      await userEvent.type(within(panel).getByLabelText('6-digit code on the terminal'), '111111');
      await userEvent.click(within(panel).getByRole('button', { name: 'Pair' }));
      expect(await within(panel).findByRole('alert')).toHaveTextContent('Too many wrong codes');
    });

    it('asks before replacing a paired terminal, then sends the confirmation', async () => {
      const bodies: { confirmReplace: boolean }[] = [];
      serve({
        'POST /api/admin/terminals/pair': ({ body }) => {
          bodies.push(body as { confirmReplace: boolean });
          return bodies.length === 1
            ? json(409, {
                status: 409,
                title: 'Counter A is already paired. The old device will be logged out.',
                code: 'already_paired',
              })
            : json(200, { terminalId: 't-Counter A', terminalName: 'Counter A' });
        },
      });
      const panel = await openPair();
      await userEvent.type(within(panel).getByLabelText('6-digit code on the terminal'), '482913');
      await userEvent.selectOptions(within(panel).getByLabelText('Terminal'), 't-Counter A');
      await userEvent.click(within(panel).getByRole('button', { name: 'Pair' }));
      const dialog = await screen.findByRole('dialog');
      expect(dialog).toHaveTextContent('The old device will be logged out');
      await userEvent.click(
        within(dialog).getByRole('button', { name: 'Pair and log out the old device' }),
      );
      expect(await within(panel).findByText(/Paired as Counter A\./)).toBeInTheDocument();
      expect(bodies.map((b) => b.confirmReplace)).toEqual([false, true]);
    });
  });

  describe('printer form', () => {
    async function openAdd() {
      renderRoutes(routes, '/admin/terminals');
      await screen.findByRole('button', { name: 'Edit Counter A' });
      await userEvent.click(screen.getByRole('button', { name: 'Add printer' }));
      return screen.getByRole('complementary');
    }

    it('shows the address fields for each connection', async () => {
      terminals[0] = terminal('Counter A', {
        localPrinters: ['EPSON TM-T20III', 'Microsoft Print to PDF'],
      });
      serve();
      const panel = await openAdd();
      expect(within(panel).getByLabelText('IP address')).toBeInTheDocument();
      expect(within(panel).getByLabelText('Port')).toHaveValue('9100');

      await userEvent.selectOptions(within(panel).getByLabelText('Connection'), 'WindowsShare');
      expect(within(panel).queryByLabelText('IP address')).not.toBeInTheDocument();
      expect(within(panel).getByLabelText('Share name')).toBeInTheDocument();

      await userEvent.selectOptions(within(panel).getByLabelText('Connection'), 'ViaTerminal');
      expect(within(panel).getByLabelText('Plugged into')).toHaveValue('t-Counter A');
      const local = within(panel).getByLabelText('Printer name on that PC');
      expect(within(local).getByRole('option', { name: 'EPSON TM-T20III' })).toBeInTheDocument();

      // The File printer is for development: a new printer isn't offered it.
      expect(
        within(within(panel).getByLabelText('Connection')).queryByRole('option', {
          name: 'File (dev)',
        }),
      ).not.toBeInTheDocument();
    });

    it('checks the connection and shows the result in plain words', async () => {
      const checks: unknown[] = [];
      serve({
        'POST /api/admin/printers/check': ({ body }) => {
          checks.push(body);
          return checks.length === 1
            ? json(200, {
                reachable: false,
                message:
                  'No answer from 192.168.1.50:9100 — is the printer on and plugged into the network?',
              })
            : json(200, { reachable: true, message: 'Reachable' });
        },
      });
      const panel = await openAdd();
      await userEvent.type(within(panel).getByLabelText('IP address'), '192.168.1.50');
      await userEvent.click(within(panel).getByRole('button', { name: 'Check connection' }));
      expect(
        await within(panel).findByText(/No answer from 192.168.1.50:9100/),
      ).toBeInTheDocument();
      expect(checks[0]).toEqual({
        connectionMode: 'Network9100',
        address: '192.168.1.50:9100',
        owningTerminalId: null,
      });

      await userEvent.click(within(panel).getByRole('button', { name: 'Check connection' }));
      expect(await within(panel).findByText('Reachable')).toBeInTheDocument();
    });
  });

  describe('failed jobs', () => {
    const job = (id: string, kind: PrintJobRow['kind']): PrintJobRow => ({
      id,
      createdAt: '2026-10-20T19:30:00Z',
      kind,
      ticketId: null,
      error: 'No answer from 192.168.1.50:9100',
      attempts: 5,
      status: 'Failed',
      printerName: 'Bar network',
    });

    it('retries, sends to another printer and discards with a reason', async () => {
      const calls: { path: string; body: unknown }[] = [];
      const record =
        (path: string) =>
        ({ body }: { body: unknown }) => {
          calls.push({ path, body });
          return json(200, {});
        };
      printers = [
        printer('Bar network', {
          id: 'p-net',
          status: 'Error',
          lastError: 'No answer',
          failedJobs: 2,
        }),
        tp1,
      ];
      serve({
        'GET /api/admin/printers/p-net/jobs': () =>
          json(200, [job('j1', 'Bill'), job('j2', 'Dispense')]),
        'POST /api/admin/print-jobs/j1/retry': record('retry j1'),
        'POST /api/admin/print-jobs/j2/redirect': record('redirect j2'),
        'POST /api/admin/print-jobs/j1/discard': record('discard j1'),
        'POST /api/admin/printers/p-net/jobs/retry-all': record('retry all'),
      });
      renderRoutes(routes, '/admin/terminals');
      await userEvent.click(await screen.findByRole('button', { name: 'Failed jobs (2)' }));
      const panel = screen.getByRole('complementary');
      const items = await within(panel).findAllByRole('listitem');
      expect(items[0]).toHaveTextContent('Bill');
      expect(items[0]).toHaveTextContent('No answer from 192.168.1.50:9100');

      await userEvent.click(within(items[0]).getByRole('button', { name: 'Retry' }));
      await userEvent.click(within(items[1]).getByRole('button', { name: 'Send' }));
      await userEvent.click(within(panel).getByRole('button', { name: 'Retry all' }));

      await userEvent.click(within(items[0]).getByRole('button', { name: 'Discard' }));
      const dialog = screen.getByRole('dialog');
      const discard = within(dialog).getByRole('button', { name: 'Discard' });
      expect(discard).toBeDisabled();
      await userEvent.type(
        within(dialog).getByLabelText('Why? (kept in the audit log)'),
        'Printed by hand',
      );
      await userEvent.click(discard);

      await waitFor(() => expect(calls).toHaveLength(4));
      expect(calls).toEqual([
        { path: 'retry j1', body: undefined },
        { path: 'redirect j2', body: { printerId: 'p-tp1' } },
        { path: 'retry all', body: undefined },
        { path: 'discard j1', body: { reason: 'Printed by hand' } },
      ]);
    });
  });
});
