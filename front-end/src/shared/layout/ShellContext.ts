import { createContext, useContext } from 'react';

/**
 * Signed-in pages still wrap themselves in AppShell. When the root layout is present it supplies
 * the sidebar once; a nested AppShell becomes transparent instead of drawing a second one.
 */
export const ShellContext = createContext(false);

export function useInRootShell(): boolean {
  return useContext(ShellContext);
}
