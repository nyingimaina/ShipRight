import type { RefObject } from 'react';

const SCROLL_PAUSE_MS = 250;

export type ZestActionOptions = {
  fallbackMessage: string;
  onError: (message: string) => void;
  errorRef: RefObject<HTMLElement | null>;
  pauseMs?: number;
};

export const errorMessage = (error: unknown, fallback: string): string =>
  (error as { message?: string } | null)?.message ?? fallback;

/**
 * Runs an action behind a ZestButton: the rejection is rethrown so Zest can show its fail response,
 * while the app shows the real server message and scrolls to it after a short pause.
 */
export async function runZestAction<T>(action: () => Promise<T>, options: ZestActionOptions): Promise<T> {
  try {
    return await action();
  } catch (error) {
    options.onError(errorMessage(error, options.fallbackMessage));
    const pause = options.pauseMs ?? SCROLL_PAUSE_MS;
    window.setTimeout(() => {
      options.errorRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' });
    }, pause);
    throw error;
  }
}
