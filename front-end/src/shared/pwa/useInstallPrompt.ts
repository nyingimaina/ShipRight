import { useCallback, useEffect, useState } from 'react';

interface BeforeInstallPromptEvent extends Event {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed'; platform: string }>;
}

type CaptureWindow = Window & { __shiprightInstallPrompt?: BeforeInstallPromptEvent };

function isStandalone(): boolean {
  if (typeof window === 'undefined') return false;
  return window.matchMedia('(display-mode: standalone)').matches
    || (window.navigator as Navigator & { standalone?: boolean }).standalone === true;
}

/**
 * The browser's install prompt, when it offers one. iOS Safari does not fire it, so callers show the Share → Add to
 * Home Screen instruction there instead.
 */
export function useInstallPrompt() {
  const [deferred, setDeferred] = useState<BeforeInstallPromptEvent | null>(null);
  const [installed, setInstalled] = useState(false);

  useEffect(() => {
    setInstalled(isStandalone());
    // The inline document script may have caught the offer before this component existed.
    const captured = (window as CaptureWindow).__shiprightInstallPrompt;
    if (captured) setDeferred(captured);

    const onPrompt = (event: Event) => {
      event.preventDefault();
      (window as CaptureWindow).__shiprightInstallPrompt = event as BeforeInstallPromptEvent;
      setDeferred(event as BeforeInstallPromptEvent);
    };
    const onInstalled = () => {
      setInstalled(true);
      setDeferred(null);
      delete (window as CaptureWindow).__shiprightInstallPrompt;
    };
    window.addEventListener('beforeinstallprompt', onPrompt);
    window.addEventListener('appinstalled', onInstalled);
    return () => {
      window.removeEventListener('beforeinstallprompt', onPrompt);
      window.removeEventListener('appinstalled', onInstalled);
    };
  }, []);

  const prompt = useCallback(async (): Promise<boolean> => {
    if (!deferred) return false;
    await deferred.prompt();
    const choice = await deferred.userChoice;
    setDeferred(null);
    delete (window as CaptureWindow).__shiprightInstallPrompt;
    if (choice.outcome === 'accepted') {
      setInstalled(true);
      return true;
    }
    return false;
  }, [deferred]);

  return { canPrompt: deferred !== null, installed, prompt };
}

export function installHint(): string {
  if (typeof navigator === 'undefined') return 'Open ShipRight in your browser and choose Install.';
  const ua = navigator.userAgent;
  if (/iPhone|iPad|iPod/i.test(ua)) return 'In Safari, tap Share, then “Add to Home Screen”.';
  if (/Android/i.test(ua)) return 'In Chrome, open the menu and choose “Install app”.';
  return 'Use the install icon in your browser’s address bar, or the menu’s “Install ShipRight”.';
}
