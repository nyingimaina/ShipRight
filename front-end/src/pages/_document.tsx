import { Html, Head, Main, NextScript } from 'next/document';

export default function Document() {
  return (
    <Html lang="en">
      <Head>
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
        <meta name="theme-color" content="#0B1120" media="(prefers-color-scheme: dark)" />
        <meta name="theme-color" content="#F0F4F8" media="(prefers-color-scheme: light)" />
        <link rel="preload" href="/fonts/manrope-latin.woff2" as="font" type="font/woff2" crossOrigin="anonymous" />
        <link rel="preload" href="/fonts/jetbrains-mono-latin.woff2" as="font" type="font/woff2" crossOrigin="anonymous" />
        <link rel="manifest" href="/manifest.webmanifest" />
        <link rel="icon" href="/favicon.ico" sizes="any" />
        <link rel="icon" href="/icon.svg" type="image/svg+xml" />
        <link rel="apple-touch-icon" href="/icons/apple-touch-icon.png" />
        <meta name="apple-mobile-web-app-capable" content="yes" />
        <meta name="mobile-web-app-capable" content="yes" />
        <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
        <meta name="apple-mobile-web-app-title" content="ShipRight" />
        <meta name="format-detection" content="telephone=no" />
        {/* Capture the browser's install offer before React hydrates; the Settings card reads it later. */}
        <script
          dangerouslySetInnerHTML={{
            __html:
              "(function(){try{window.addEventListener('beforeinstallprompt',function(e){e.preventDefault();window.__shiprightInstallPrompt=e;});}catch(_){}})();",
          }}
        />
      </Head>
      <body>
        <Main />
        <NextScript />
      </body>
    </Html>
  );
}
