import { create } from 'zustand';

export type Role = 'Admin' | 'Operator' | 'Viewer';

export interface CompanyChoice {
  id: string;
  name: string;
  role: Role;
  isActive: boolean;
  current: boolean;
}

export interface User {
  id: string;
  email: string;
  name: string;
  companyId: string;
  /** Filled from the server session; optional because older sessions may not carry it. */
  companyName?: string;
  role: Role;
  mustChangePassword: boolean;
  /** Every company this person may enter; optional because older servers omit it. */
  companies?: CompanyChoice[];
  /** The person's chosen IANA zone; empty means the app default. */
  timeZone?: string;
}

export interface AuthState {
  user: User | null;
  isAuthenticated: boolean;
  setUser: (user: User | null) => void;
  clearAuth: () => void;
}

// The sign-in session lives in the server's httpOnly cookie; this store only mirrors the person
// returned by /api/auth/me so the UI can gate screens and nav. Nothing secret is kept in the browser.
export const useAuthStore = create<AuthState>()((set) => ({
  user: null,
  isAuthenticated: false,
  setUser: (user) => {
    set({ user, isAuthenticated: user !== null });
  },
  clearAuth: () => {
    set({ user: null, isAuthenticated: false });
  },
}));