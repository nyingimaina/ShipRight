import { createContext, useCallback, useContext, useEffect, useState } from 'react';

export type ThemeChoice = 'dark' | 'light' | 'system';

interface ThemeContextValue {
  theme: ThemeChoice;
  setTheme: (t: ThemeChoice) => void;
}

const ThemeContext = createContext<ThemeContextValue>({
  theme: 'system',
  setTheme: () => {},
});

const STORAGE_KEY = 'shipright-theme';

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  const [theme, setThemeState] = useState<ThemeChoice>('system');

  // Apply data-theme to <html> and persist
  const applyTheme = useCallback((choice: ThemeChoice) => {
    const html = document.documentElement;
    if (choice === 'system') {
      html.removeAttribute('data-theme');
    } else {
      html.setAttribute('data-theme', choice);
    }
    localStorage.setItem(STORAGE_KEY, choice);
    setThemeState(choice);
  }, []);

  // Hydrate from localStorage on first render
  useEffect(() => {
    const stored = localStorage.getItem(STORAGE_KEY) as ThemeChoice | null;
    if (stored === 'dark' || stored === 'light' || stored === 'system') {
      applyTheme(stored);
    }
  }, [applyTheme]);

  return (
    <ThemeContext.Provider value={{ theme, setTheme: applyTheme }}>
      {children}
    </ThemeContext.Provider>
  );
}

export function useTheme() {
  return useContext(ThemeContext);
}
