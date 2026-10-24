export type PermissionValue = 'Allowed' | 'NeedsApproval' | 'NotAllowed';

export interface PermissionKeyInfo {
  key: string;
  section: string;
  label: string;
  help: string;
  /** False for keys a supervisor can't lend with a PIN: only Allowed or Not allowed. */
  approvable: boolean;
}

export interface RoleInfo {
  id: string;
  name: string;
  systemKey: string | null;
  /** Active users with this role. */
  users: string[];
  rowVersion: number;
}

export interface PermissionsGrid {
  version: string;
  roles: RoleInfo[];
  keys: PermissionKeyInfo[];
  cells: Record<string, Record<string, PermissionValue>>;
}

export interface PermissionChange {
  at: string;
  who: string;
  what: string;
  role: string | null;
  key: string | null;
  old: string | null;
  new: string | null;
}

export const permissionsKey = ['permissions', 'grid'] as const;
