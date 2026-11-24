import { createRef } from 'react';
import { runZestAction } from '../zestAction';

describe('runZestAction', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('passes a successful result through without showing an error or scrolling', async () => {
    const onError = jest.fn();
    const scrollIntoView = jest.fn();
    const errorRef = createRef<HTMLDivElement>();
    Object.defineProperty(errorRef, 'current', { value: { scrollIntoView } });

    await expect(runZestAction(async () => 42, {
      fallbackMessage: 'Failed.', onError, errorRef,
    })).resolves.toBe(42);

    expect(onError).not.toHaveBeenCalled();
    jest.advanceTimersByTime(1000);
    expect(scrollIntoView).not.toHaveBeenCalled();
  });

  it('reports the server message, rejects so Zest shows its fail state, then scrolls to the error', async () => {
    const onError = jest.fn();
    const scrollIntoView = jest.fn();
    const errorRef = createRef<HTMLDivElement>();
    Object.defineProperty(errorRef, 'current', { value: { scrollIntoView } });
    const failure = new Error('Request failed. Check logs for request ID abc.');

    await expect(runZestAction(async () => { throw failure; }, {
      fallbackMessage: 'Import failed.', onError, errorRef,
    })).rejects.toBe(failure);

    expect(onError).toHaveBeenCalledWith('Request failed. Check logs for request ID abc.');
    expect(scrollIntoView).not.toHaveBeenCalled();

    jest.advanceTimersByTime(249);
    expect(scrollIntoView).not.toHaveBeenCalled();
    jest.advanceTimersByTime(1);
    expect(scrollIntoView).toHaveBeenCalledWith({ behavior: 'smooth', block: 'nearest' });
  });

  it('uses the fallback when the failure carries no message', async () => {
    const onError = jest.fn();
    const errorRef = createRef<HTMLDivElement>();

    await expect(runZestAction(async () => { throw {}; }, {
      fallbackMessage: 'Export failed.', onError, errorRef,
    })).rejects.toBeDefined();

    expect(onError).toHaveBeenCalledWith('Export failed.');
  });
});
