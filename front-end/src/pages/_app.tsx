import type { AppProps } from 'next/app';
import { Inter } from 'next/font/google';
import { Toaster } from 'react-hot-toast';
import { ThemeProvider } from '@/shared/theme/ThemeContext';
import AppChromeProviders from '@/shared/theme/AppChromeProviders';
import AuthGuard from '@/shared/auth/AuthGuard';
import RootLayout from '@/shared/layout/RootLayout';
import '@/styles/globals.css';

const inter = Inter({ subsets: ['latin'], variable: '--font-inter' });

export default function App({ Component, pageProps }: AppProps) {
  return (
    <ThemeProvider>
      <AppChromeProviders>
        <div className={inter.className}>
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