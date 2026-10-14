import type { AppProps } from 'next/app';
import { Inter } from 'next/font/google';
import { Toaster } from 'react-hot-toast';
import { ThemeProvider } from '@/shared/theme/ThemeContext';
import AuthGuard from '@/shared/auth/AuthGuard';
import '@/styles/globals.css';

const inter = Inter({ subsets: ['latin'], variable: '--font-inter' });

export default function App({ Component, pageProps }: AppProps) {
  return (
    <ThemeProvider>
      <div className={inter.className}>
        <AuthGuard>
          <Component {...pageProps} />
        </AuthGuard>
        <Toaster position="bottom-right" />
      </div>
    </ThemeProvider>
  );
}