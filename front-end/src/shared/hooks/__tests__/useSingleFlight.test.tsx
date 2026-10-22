import { render, screen, fireEvent, act } from '@testing-library/react';
import React from 'react';
import { useSingleFlight } from '../useSingleFlight';

function deferred() {
  let resolve!: () => void; let reject!: (e: unknown) => void;
  const promise = new Promise<void>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function Harness({ work }: { work: () => Promise<void> }) {
  const { run, busy } = useSingleFlight();
  return (
    <div>
      <button onClick={() => { run(work).catch(() => {}); }}>go</button>
      <span data-testid="busy">{String(busy)}</span>
    </div>
  );
}

describe('useSingleFlight', () => {
  it('ignores repeat calls while one is in flight (rage-clicking)', async () => {
    const d = deferred();
    const work = jest.fn(() => d.promise);
    render(<Harness work={work} />);
    const btn = screen.getByText('go');
    // three clicks in the same tick, before React re-renders
    act(() => { fireEvent.click(btn); fireEvent.click(btn); fireEvent.click(btn); });
    expect(work).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('busy').textContent).toBe('true');
    await act(async () => { d.resolve(); });
    expect(screen.getByTestId('busy').textContent).toBe('false');
  });

  it('allows a new call once the previous one finished', async () => {
    const work = jest.fn().mockResolvedValue(undefined);
    render(<Harness work={work} />);
    await act(async () => { fireEvent.click(screen.getByText('go')); });
    await act(async () => { fireEvent.click(screen.getByText('go')); });
    expect(work).toHaveBeenCalledTimes(2);
  });

  it('releases the lock when the work fails', async () => {
    const d = deferred();
    const work = jest.fn().mockReturnValueOnce(d.promise).mockResolvedValue(undefined);
    render(<Harness work={work} />);
    act(() => { fireEvent.click(screen.getByText('go')); });
    await act(async () => { d.reject(new Error('boom')); });
    expect(screen.getByTestId('busy').textContent).toBe('false');
    await act(async () => { fireEvent.click(screen.getByText('go')); });
    expect(work).toHaveBeenCalledTimes(2);
  });
});
