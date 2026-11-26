import { useAuthStore } from '@/shared/auth';

/** The app default, used until a person picks their own zone. */
export const DEFAULT_TIME_ZONE = 'Africa/Nairobi';

/** Every screen formats dates through this, so one preference changes the whole app. */
export function activeTimeZone(): string {
  return useAuthStore.getState().user?.timeZone || DEFAULT_TIME_ZONE;
}

export function isValidTimeZone(id: string): boolean {
  try {
    new Intl.DateTimeFormat('en', { timeZone: id });
    return true;
  } catch {
    return false;
  }
}

/** A short, friendly name such as "Nairobi". */
export function timeZoneLabel(id: string): string {
  const parts = id.split('/');
  return (parts[parts.length - 1] ?? id).replace(/_/g, ' ');
}

export function formatDateTime(
  value: string | number | Date | null | undefined,
  timeZone = activeTimeZone(),
  locale?: string,
): string {
  if (value === null || value === undefined || value === '') return '—';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat(locale, {
    timeZone,
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(date);
}

export function formatDate(
  value: string | number | Date | null | undefined,
  timeZone = activeTimeZone(),
  locale?: string,
): string {
  if (value === null || value === undefined || value === '') return '—';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat(locale, {
    timeZone,
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  }).format(date);
}

/** "in 3 hours", "2 days ago" — safe in any zone because it measures elapsed time. */
export function formatRelative(
  value: string | number | Date | null | undefined,
  now: Date = new Date(),
  locale?: string,
): string {
  if (value === null || value === undefined || value === '') return '—';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return '—';

  const seconds = Math.round((date.getTime() - now.getTime()) / 1000);
  const formatter = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 31_536_000],
    ['month', 2_592_000],
    ['week', 604_800],
    ['day', 86_400],
    ['hour', 3_600],
    ['minute', 60],
    ['second', 1],
  ];

  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size || unit === 'second') {
      return formatter.format(Math.round(seconds / size), unit);
    }
  }
  return 'now';
}

/** "It's 4:23 PM in Nairobi right now." */
export function localTimeIn(timeZone: string, now: Date = new Date()): string {
  return new Intl.DateTimeFormat(undefined, { timeZone, hour: 'numeric', minute: '2-digit' }).format(now);
}

/** The zones the picker offers, with the app default first and browser-known zones after it. */
export function timeZoneChoices(): string[] {
  const supported = typeof Intl.supportedValuesOf === 'function'
    ? (Intl.supportedValuesOf('timeZone') as string[])
    : [];
  const browser = Intl.DateTimeFormat().resolvedOptions().timeZone;
  return Array.from(new Set([DEFAULT_TIME_ZONE, browser, ...supported].filter(isValidTimeZone)));
}
