import { create } from 'zustand';

export type Role = 'Admin' | 'Operator' | 'Viewer';

export interface User {
  id: string;
  email: string;
  name: string;
  companyId: string;
  role: Role;
  mustChangePassword: boolean;
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