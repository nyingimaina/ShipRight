import { useEffect } from 'react';
import type { AppProps } from 'next/app';
import { Inter } from 'next/font/google';
import { Toaster } from 'react-hot-toast';
import { ThemeProvider } from '@/shared/theme/ThemeContext';
import { useAuthStore } from '@/shared/auth';
import { setAccessToken } from '@/shared/ApiService';
import '@/styles/globals.css';

const inter = Inter({ subsets: ['latin'], variable: '--font-inter' });

export default function App({ Component, pageProps }: AppProps) {
  const accessToken = useAuthStore((s) => s.accessToken);

  useEffect(() => {
    setAccessToken(accessToken);
  }, [accessToken]);

  return (
    <ThemeProvider>
      <div className={inter.className}>
        <Component {...pageProps} />
        <Toaster position="bottom-right" />
      </div>
    </ThemeProvider>
  );
}
