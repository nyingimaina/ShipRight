import type { ReactNode } from 'react';
import { ZestButtonConfigProvider } from 'jattac.libs.web.zest-button';
import { ZestLayoutThemeProvider } from 'jattac.libs.web.zest-responsive-layout';
import { useTheme } from './ThemeContext';

const buttonConfig = {
  defaultProps: {
    visualOptions: { size: 'md' as const },
    busyOptions: { preventRageClick: true, minBusyDurationMs: 450 },
    successOptions: { showCheckmark: true, autoResetAfterMs: 1200 },
  },
};

/** Shared jattac theme and interaction defaults, kept in one place above every page. */
export default function AppChromeProviders({ children }: { children: ReactNode }) {
  const { theme } = useTheme();

  return (
    <ZestLayoutThemeProvider theme={theme}>
      <ZestButtonConfigProvider config={buttonConfig}>
        {children}
      </ZestButtonConfigProvider>
    </ZestLayoutThemeProvider>
  );
}
