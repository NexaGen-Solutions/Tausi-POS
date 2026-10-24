import type { PermissionKeyInfo, PermissionValue, PermissionsGrid, RoleInfo } from './types';

/** The next value when a cell is tapped; keys that can't be approved skip Needs approval. */
export function nextValue(value: PermissionValue, approvable: boolean): PermissionValue {
  if (value === 'Allowed') return approvable ? 'NeedsApproval' : 'NotAllowed';
  if (value === 'NeedsApproval') return 'NotAllowed';
  return 'Allowed';
}

/** The Admin role keeps permissions.manage, so nobody can lock everyone out. */
export const isLocked = (role: RoleInfo, key: string) =>
  role.systemKey === 'admin' && key === 'permissions.manage';

export const cellId = (roleId: string, key: string) => `${roleId}:${key}`;

/** A cell's value with the staged change on top. */
export function valueOf(
  grid: PermissionsGrid,
  staged: Record<string, PermissionValue>,
  roleId: string,
  key: string,
): PermissionValue {
  return staged[cellId(roleId, key)] ?? grid.cells[roleId]?.[key] ?? 'NotAllowed';
}

/** For each approvable key: the roles that can approve it, and whether some role needs that approval. */
export function approvers(grid: PermissionsGrid, staged: Record<string, PermissionValue>) {
  return grid.keys
    .filter((k) => k.approvable)
    .map((k: PermissionKeyInfo) => {
      const allowed = grid.roles.filter((r) => valueOf(grid, staged, r.id, k.key) === 'Allowed');
      const needs = grid.roles.some((r) => valueOf(grid, staged, r.id, k.key) === 'NeedsApproval');
      return {
        info: k,
        roles: allowed.map((r) => r.name),
        needs,
        nobody: needs && allowed.length === 0,
      };
    });
}
