import { getBrowserTimeZone } from '../timeZone';
import { DEFAULT_TIME_ZONE } from '../time';

describe('getBrowserTimeZone', () => {
  it('returns the browser IANA timezone', () => {
    const resolvedOptions = jest.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions')
      .mockReturnValue({ timeZone: 'America/New_York' } as Intl.ResolvedDateTimeFormatOptions);

    expect(getBrowserTimeZone()).toBe('America/New_York');

    resolvedOptions.mockRestore();
  });

  it('falls back to the app default when the browser does not provide a timezone', () => {
    const resolvedOptions = jest.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions')
      .mockReturnValue({ timeZone: '' } as Intl.ResolvedDateTimeFormatOptions);

    expect(getBrowserTimeZone()).toBe(DEFAULT_TIME_ZONE);
    expect(DEFAULT_TIME_ZONE).toBe('Africa/Nairobi');

    resolvedOptions.mockRestore();
  });
});
