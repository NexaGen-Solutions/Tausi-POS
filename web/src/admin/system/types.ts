export interface Health {
  status: 'ok' | 'degraded';
  version: string;
  server: string;
  database: string;
  diskFreeBytes: number | null;
  lastBackupAt: string | null;
  backup: string;
  backupMessage: string | null;
  licence: string | null;
  minimumTerminalVersion: string;
}

export interface SystemInfo {
  version: string;
  platform: string;
  minimumTerminalVersion: string;
  cantInstallReason: string | null;
}

export interface UpdateCheck {
  current: string;
  latest: string | null;
  available: boolean;
  notes: string | null;
  canInstallHere: boolean;
  cantInstallReason: string | null;
  dayOpen: boolean;
  error: string | null;
}

export interface BackupRecord {
  name: string;
  createdAt: string;
  reason: 'Nightly' | 'Manual' | 'Upgrade' | 'BeforeRestore' | 'Command';
  size: number;
  verified: boolean;
  encrypted: boolean;
  business: string | null;
  version: string;
  copiedTo: string | null;
  copyError: string | null;
}

export interface BackupSettings {
  time: string;
  copyFolder: string | null;
  encrypt: boolean;
  hasPassword: boolean;
}

export interface BackupContents {
  business: string;
  createdAt: string;
  version: string;
  schema: string;
  provider: string;
  encrypted: boolean;
}

export function size(bytes: number | null): string {
  if (bytes === null) return '-';
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

export function when(iso: string | null): string {
  return iso
    ? new Date(iso).toLocaleString('en-GB', {
        day: 'numeric',
        month: 'short',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      })
    : '-';
}
