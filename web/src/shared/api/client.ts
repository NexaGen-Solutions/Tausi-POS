import { deviceToken } from '../device/device';
import { updateConnection } from '../connection/status';
import { clientVersion, setServerMode, setServerVersion } from '../connection/serverState';
import { endPinSession, pinSession, touchSession } from '../session/session';

/** The problem-details body the server sends for errors. */
export interface Problem {
  status: number;
  title?: string;
  detail?: string;
  approvalRequired?: boolean;
  /** A stable reason, e.g. "session_expired", "pin_taken". */
  code?: string;
  permission?: string;
  attemptsLeft?: number | null;
  lockedUntil?: string;
  retryAfterSeconds?: number;
  /** Errors per field (a setting key, or "roleId:key" for a permission cell). */
  fieldErrors?: Record<string, string>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: Problem;

  constructor(problem: Problem) {
    super(problem.detail ?? problem.title ?? `Request failed (${problem.status})`);
    this.status = problem.status;
    this.problem = problem;
  }
}

/** The server could not be reached at all. */
export class OfflineError extends Error {
  constructor() {
    super('No connection to the server.');
  }
}

export type ApprovalKind =
  'Void' | 'Gift' | 'Discount' | 'PriceChange' | 'Reprint' | 'Move' | 'Merge';

/**
 * What to ask for instead, when no supervisor is at hand ("Option 2 · supervisor elsewhere"):
 * the body of POST /api/pos/approvals without the preferred approver.
 */
export interface ApprovalContext {
  kind: ApprovalKind;
  ticketId: string;
  lineId?: string | null;
  payload: unknown;
}

/** A request for approval that was sent instead of doing the action now. */
export interface SentRequest {
  id: string;
  kind: string;
  summary: string;
  status: string;
}

/** The action wasn't done: a request was sent to a supervisor instead (not an error to show). */
export class RequestSentError extends Error {
  readonly request: SentRequest;

  constructor(request: SentRequest) {
    super('Request sent.');
    this.request = request;
  }
}

/**
 * Asks a supervisor for approval; resolves to an approval token, a request sent instead (when
 * a context was given), or null if it was not given.
 */
export type ApprovalHandler = (
  permission: string,
  context?: ApprovalContext,
) => Promise<string | { requested: SentRequest } | null>;

let approvalHandler: ApprovalHandler | null = null;
let baseUrl = '';

export function setApprovalHandler(handler: ApprovalHandler | null): void {
  approvalHandler = handler;
}

/** Normally '' (same origin). The terminal app's setup screen talks to the server it was given. */
export function setApiBase(url: string): void {
  baseUrl = url.replace(/\/+$/, '');
}

export function apiUrl(path: string): string {
  return baseUrl + path;
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal;
  /** Set when the caller retries a write itself, so the server sees the same request. */
  idempotencyKey?: string;
  /** A POST that only reads (it carries filters, e.g. running a report): no Idempotency-Key. */
  readOnly?: boolean;
  /** Let the request finish while the page unloads (e.g. giving back a ticket's edit lock). */
  keepalive?: boolean;
  /** A supervisor's approval token already in hand (their PIN typed in the action's own pop-up). */
  approvalToken?: string;
  /** Lets the approval pad offer "send a request" as well as the PIN. */
  approval?: ApprovalContext;
}

/**
 * Calls the API. Adds the device token, the PIN session and, on writes, an Idempotency-Key.
 * When the server answers "approval required", opens the approval pad, then repeats the request
 * once with the approval token (and the same Idempotency-Key).
 */
export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const method = options.method ?? 'GET';
  const idempotencyKey =
    method === 'GET' || options.readOnly
      ? undefined
      : (options.idempotencyKey ?? crypto.randomUUID());

  let response = await send(path, method, options, idempotencyKey, options.approvalToken);
  if (response.status === 403) {
    const problem = await readProblem(response);
    if (
      problem.approvalRequired &&
      problem.permission &&
      approvalHandler &&
      !options.approvalToken
    ) {
      const answer = await approvalHandler(problem.permission, options.approval);
      if (!answer) throw new ApiError(problem);
      if (typeof answer !== 'string') throw new RequestSentError(answer.requested);
      response = await send(path, method, options, idempotencyKey, answer);
    } else {
      throw new ApiError(problem);
    }
  }

  if (!response.ok) {
    const problem = await readProblem(response);
    // A new install answers only the setup wizard until it is set up.
    if (
      problem.status === 423 &&
      problem.code === 'setup_required' &&
      location.pathname !== '/setup'
    )
      location.assign('/setup');
    // A backup is being restored, or this terminal app is too old for the server: say so over everything.
    if (problem.status === 503 && problem.code === 'server_restoring') setServerMode('restoring');
    if (problem.status === 426 && problem.code === 'terminal_update_required')
      setServerMode('updating');
    // The server ended this PIN session (idle, logged out elsewhere, too old): back to the PIN pad.
    if (problem.status === 401 && problem.code === 'session_expired') endPinSession('expired');
    throw new ApiError(problem);
  }
  if (response.status === 204) return undefined as T;
  const type = response.headers.get('content-type') ?? '';
  return (type.includes('json') ? await response.json() : await response.text()) as T;
}

/** A file the server sends back (Excel, CSV, PDF), with the name it suggests. */
export interface ApiFile {
  blob: Blob;
  fileName: string;
}

/** Calls the API for a file. Errors come back as {@link ApiError}s, like {@link api}. */
export async function apiFile(path: string, options: RequestOptions = {}): Promise<ApiFile> {
  const method = options.method ?? 'GET';
  const idempotencyKey =
    method === 'GET' || options.readOnly
      ? undefined
      : (options.idempotencyKey ?? crypto.randomUUID());
  const response = await send(path, method, options, idempotencyKey, options.approvalToken);
  if (!response.ok) throw new ApiError(await readProblem(response));
  const disposition = response.headers.get('content-disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  return {
    blob: await response.blob(),
    fileName: match ? decodeURIComponent(match[1]) : 'download',
  };
}

async function send(
  path: string,
  method: string,
  options: RequestOptions,
  idempotencyKey?: string,
  approvalToken?: string,
): Promise<Response> {
  const headers = new Headers({ Accept: 'application/json', 'X-Tausi-Version': clientVersion() });
  const device = deviceToken();
  if (device) headers.set('X-Device-Token', device);
  const session = pinSession();
  if (session) {
    headers.set('Authorization', `Bearer ${session.token}`);
    touchSession();
  }
  if (idempotencyKey) headers.set('Idempotency-Key', idempotencyKey);
  if (approvalToken) headers.set('X-Approval-Token', approvalToken);

  let body: BodyInit | undefined;
  if (options.body instanceof FormData) {
    body = options.body;
  } else if (options.body !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(options.body);
  }

  try {
    const response = await fetch(apiUrl(path), {
      method,
      headers,
      body,
      credentials: 'same-origin',
      signal: options.signal,
      keepalive: options.keepalive,
    });
    updateConnection({ apiReachable: response.status !== 502 && response.status !== 503 });
    setServerVersion(response.headers.get('X-Tausi-Server-Version'));
    return response;
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    updateConnection({ apiReachable: false });
    throw new OfflineError();
  }
}

async function readProblem(response: Response): Promise<Problem> {
  try {
    const body = (await response.clone().json()) as Partial<Problem>;
    return { ...body, status: response.status };
  } catch {
    return { status: response.status, title: response.statusText };
  }
}
