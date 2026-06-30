import { render, screen, act } from '@testing-library/react';
import { ThemeProvider, useTheme, type ThemeChoice } from '../ThemeContext';

function TestConsumer() {
  const { theme, setTheme } = useTheme();
  return (
    <div>
      <span data-testid="theme">{theme}</span>
      <button onClick={() => setTheme('dark')}>dark</button>
      <button onClick={() => setTheme('light')}>light</button>
      <button onClick={() => setTheme('system')}>system</button>
    </div>
  );
}

const localStorageMock = (() => {
  let store: Record<string, string> = {};
  return {
    getItem: (k: string) => store[k] ?? null,
    setItem: (k: string, v: string) => { store[k] = v; },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { store = {}; },
  };
})();
Object.defineProperty(window, 'localStorage', { value: localStorageMock });

beforeEach(() => {
  localStorageMock.clear();
  document.documentElement.removeAttribute('data-theme');
});

describe('ThemeProvider', () => {
  it('defaults to system theme', () => {
    render(<ThemeProvider><TestConsumer /></ThemeProvider>);
    expect(screen.getByTestId('theme').textContent).toBe('system');
  });

  it('switching to dark sets data-theme="dark" on <html>', () => {
    render(<ThemeProvider><TestConsumer /></ThemeProvider>);
    act(() => { screen.getByText('dark').click(); });
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(screen.getByTestId('theme').textContent).toBe('dark');
  });

  it('switching to light sets data-theme="light" on <html>', () => {
    render(<ThemeProvider><TestConsumer /></ThemeProvider>);
    act(() => { screen.getByText('light').click(); });
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });

  it('switching to system removes data-theme from <html>', () => {
    document.documentElement.setAttribute('data-theme', 'dark');
    render(<ThemeProvider><TestConsumer /></ThemeProvider>);
    act(() => { screen.getByRole('button', { name: 'system' }).click(); });
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('persists theme choice to localStorage', () => {
    render(<ThemeProvider><TestConsumer /></ThemeProvider>);
    act(() => { screen.getByText('light').click(); });
    expect(localStorageMock.getItem('shipright-theme')).toBe('light');
  });

  it('restores theme from localStorage on mount', () => {
    localStorageMock.setItem('shipright-theme', 'dark');
    render(<ThemeProvider><TestConsumer /></ThemeProvider>);
    expect(screen.getByTestId('theme').textContent).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('ignores invalid localStorage values', () => {
    localStorageMock.setItem('shipright-theme', 'invalid-theme' as ThemeChoice);
    render(<ThemeProvider><TestConsumer /></ThemeProvider>);
    // Should fall back to default 'system', not crash
    expect(screen.getByTestId('theme').textContent).toBe('system');
  });
});
