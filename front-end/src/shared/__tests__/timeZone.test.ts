import { getBrowserTimeZone } from '../timeZone';

describe('getBrowserTimeZone', () => {
  it('returns the browser IANA timezone', () => {
    const resolvedOptions = jest.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions')
      .mockReturnValue({ timeZone: 'America/New_York' } as Intl.ResolvedDateTimeFormatOptions);

    expect(getBrowserTimeZone()).toBe('America/New_York');

    resolvedOptions.mockRestore();
  });

  it('falls back to UTC when the browser does not provide a timezone', () => {
    const resolvedOptions = jest.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions')
      .mockReturnValue({ timeZone: '' } as Intl.ResolvedDateTimeFormatOptions);

    expect(getBrowserTimeZone()).toBe('UTC');

    resolvedOptions.mockRestore();
  });
});
