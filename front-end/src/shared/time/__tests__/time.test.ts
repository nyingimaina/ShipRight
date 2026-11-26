import { DEFAULT_TIME_ZONE, formatDate, formatDateTime, formatRelative, isValidTimeZone, localTimeIn } from '../index';

describe('time helpers', () => {
  const instant = '2026-07-01T09:30:00.000Z';

  it('defaults to Nairobi', () => {
    expect(DEFAULT_TIME_ZONE).toBe('Africa/Nairobi');
  });

  it('formats the same instant in the person’s zone', () => {
    expect(formatDateTime(instant, 'Africa/Nairobi', 'en-US')).toMatch(/Jul 1, 2026/);
    expect(formatDateTime(instant, 'Africa/Nairobi', 'en-US')).toMatch(/12:30/);
    expect(formatDateTime(instant, 'Europe/London', 'en-US')).toMatch(/10:30/);
  });

  it('formats a date without a time', () => {
    expect(formatDate(instant, 'Africa/Nairobi', 'en-US')).toMatch(/Jul 1, 2026/);
  });

  it('never shows Invalid Date', () => {
    expect(formatDateTime('not-a-date')).toBe('—');
    expect(formatDate(null)).toBe('—');
  });

  it('describes an elapsed time the same way in every zone', () => {
    const now = new Date('2026-07-01T12:30:00.000Z');
    expect(formatRelative(instant, now)).toBe('3 hours ago');
  });

  it('shows the current local time for a zone', () => {
    expect(localTimeIn('Africa/Nairobi', new Date(instant))).toMatch(/12:30/);
  });

  it('recognises real zones and rejects made-up ones', () => {
    expect(isValidTimeZone('Africa/Nairobi')).toBe(true);
    expect(isValidTimeZone('Mars/Olympus_Mons')).toBe(false);
  });
});
