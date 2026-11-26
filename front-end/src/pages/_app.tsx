import type { AppProps } from 'next/app';
import { useEffect } from 'react';
import { JetBrains_Mono, Manrope } from 'next/font/google';
import { Toaster } from 'react-hot-toast';
import { ThemeProvider } from '@/shared/theme/ThemeContext';
import AppChromeProviders from '@/shared/theme/AppChromeProviders';
import AuthGuard from '@/shared/auth/AuthGuard';
import RootLayout from '@/shared/layout/RootLayout';
import '@/styles/globals.css';

// Manrope gives the interface a modern, slightly geometric voice; JetBrains Mono keeps code and identifiers honest.
const manrope = Manrope({ subsets: ['latin'], variable: '--font-sans', display: 'swap' });
const jetbrains = JetBrains_Mono({ subsets: ['latin'], variable: '--font-mono', display: 'swap' });

export default function App({ Component, pageProps }: AppProps) {
  useEffect(() => {
    // The installable shell is a production concern; dev keeps its fast refresh behaviour untouched.
    if (process.env.NODE_ENV !== 'production' || !('serviceWorker' in navigator)) return;
    const register = () => {
      navigator.serviceWorker.register('/sw.js').catch(() => {
        /* a failed registration must never break the app */
      });
    };
    if (document.readyState === 'complete') register();
    else {
      window.addEventListener('load', register, { once: true });
      return () => window.removeEventListener('load', register);
    }
  }, []);

  return (
    <ThemeProvider>
      <AppChromeProviders>
        <div id="app-root" className={`${manrope.variable} ${jetbrains.variable} ${manrope.className}`}>
          <AuthGuard>
            <RootLayout>
              <Component {...pageProps} />
            </RootLayout>
          </AuthGuard>
          <Toaster position="bottom-right" />
        </div>
      </AppChromeProviders>
    </ThemeProvider>
  );
}