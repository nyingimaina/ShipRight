import { render, screen, fireEvent, act } from '@testing-library/react';
import React from 'react';
import StreamLog, { StreamLine } from '../StreamLog';

const L = (id: number, text: string, kind?: StreamLine['kind']): StreamLine => ({ id, text, kind });

beforeEach(() => {
  Object.assign(navigator, { clipboard: { writeText: jest.fn().mockResolvedValue(undefined) } });
});

describe('StreamLog', () => {
  it('shows the empty text and no lines', () => {
    render(<StreamLog lines={[]} emptyText="Nothing yet" />);
    expect(screen.getByText('Nothing yet')).toBeInTheDocument();
  });

  it('renders lines and a count, or a streaming indicator when live', () => {
    const { rerender } = render(<StreamLog lines={[L(1, 'a'), L(2, 'b')]} />);
    expect(screen.getByText('a')).toBeInTheDocument();
    expect(screen.getByText('2 lines')).toBeInTheDocument();
    rerender(<StreamLog lines={[L(1, 'a')]} isLive />);
    expect(screen.getByText(/streaming/)).toBeInTheDocument();
  });

  it('copies ALL lines, even those hidden by a filter, and confirms', async () => {
    render(<StreamLog lines={[L(1, 'alpha'), L(2, 'beta')]} />);
    fireEvent.change(screen.getByPlaceholderText('Filter…'), { target: { value: 'alpha' } });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Copy all' })); });
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith('alpha\nbeta');
    expect(screen.getByText('Copied')).toBeInTheDocument();
  });

  it('falls back to execCommand when the clipboard API is unavailable (plain http)', async () => {
    Object.assign(navigator, { clipboard: undefined });
    (document as any).execCommand = jest.fn().mockReturnValue(true);
    render(<StreamLog lines={[L(1, 'alpha')]} />);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Copy all' })); });
    expect((document as any).execCommand).toHaveBeenCalledWith('copy');
    expect(screen.getByText('Copied')).toBeInTheDocument();
  });

  it('filters by text (case-insensitive)', () => {
    render(<StreamLog lines={[L(1, 'Docker pull'), L(2, 'git pull')]} />);
    fireEvent.change(screen.getByPlaceholderText('Filter…'), { target: { value: 'DOCKER' } });
    expect(screen.getByText('Docker pull')).toBeInTheDocument();
    expect(screen.queryByText('git pull')).toBeNull();
  });

  it('errors-only keeps error kinds and error-looking lines', () => {
    render(<StreamLog lines={[L(1, 'fine'), L(2, '✗ Deploy failed', 'error'), L(3, 'fatal: boom')]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Errors only' }));
    expect(screen.queryByText('fine')).toBeNull();
    expect(screen.getByText('✗ Deploy failed')).toBeInTheDocument();
    expect(screen.getByText('fatal: boom')).toBeInTheDocument();
  });

  it('pausing stops following and offers "Jump to latest" with a new-line count', () => {
    const { rerender } = render(<StreamLog lines={[L(1, 'a')]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Pause auto-scroll' }));
    rerender(<StreamLog lines={[L(1, 'a'), L(2, 'b'), L(3, 'c')]} />);
    fireEvent.click(screen.getByRole('button', { name: /Jump to latest \(2 new\)/ }));
    expect(screen.queryByRole('button', { name: /Jump to latest/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Pause auto-scroll' })).toBeInTheDocument();
  });

  it('auto-pauses when the user scrolls up', () => {
    render(<StreamLog lines={[L(1, 'a')]} />);
    const log = screen.getByTestId('stream-log-body');
    Object.defineProperty(log, 'scrollHeight', { value: 1000, configurable: true });
    Object.defineProperty(log, 'clientHeight', { value: 200, configurable: true });
    log.scrollTop = 100;
    fireEvent.scroll(log);
    expect(screen.getByRole('button', { name: 'Resume auto-scroll' })).toBeInTheDocument();
  });

  it('downloads using the given filename', () => {
    (URL as any).createObjectURL = jest.fn(() => 'blob:x');
    (URL as any).revokeObjectURL = jest.fn();
    let anchor: HTMLAnchorElement | undefined;
    const orig = document.createElement.bind(document);
    jest.spyOn(document, 'createElement').mockImplementation((tag: string) => {
      const el = orig(tag);
      if (tag === 'a') { anchor = el as HTMLAnchorElement; anchor.click = jest.fn(); }
      return el;
    });
    render(<StreamLog lines={[L(1, 'a')]} filename="prod-prepare.log" />);
    fireEvent.click(screen.getByRole('button', { name: 'Download log' }));
    expect(anchor?.download).toBe('prod-prepare.log');
    expect(anchor?.click).toHaveBeenCalled();
    jest.restoreAllMocks();
  });

  it('Clear is offered only when onClear is given', () => {
    const onClear = jest.fn();
    const { rerender } = render(<StreamLog lines={[L(1, 'a')]} />);
    expect(screen.queryByRole('button', { name: 'Clear' })).toBeNull();
    rerender(<StreamLog lines={[L(1, 'a')]} onClear={onClear} />);
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(onClear).toHaveBeenCalled();
  });
});
