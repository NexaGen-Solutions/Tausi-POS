export interface StaffRole {
  id: string;
  name: string;
  canUseAdminSite: boolean;
}

export interface StaffMember {
  id: string;
  name: string;
  roleId: string;
  roleName: string;
  active: boolean;
  mustChangePin: boolean;
  adminUserName: string | null;
  hasAdminPassword: boolean;
  lastLogin: { at: string; terminalName: string | null } | null;
  terminalsLast7Days: string[];
  rowVersion: number;
}

export interface StaffList {
  members: StaffMember[];
  assignableRoles: StaffRole[];
}

export interface StaffSaved {
  member: StaffMember;
  temporaryPin: string | null;
}

export type StatusFilter = 'active' | 'inactive' | 'all';
