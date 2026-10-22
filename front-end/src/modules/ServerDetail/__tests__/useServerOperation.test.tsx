import { render, screen, fireEvent, act, waitFor } from '@testing-library/react';
import React from 'react';
import { useServerOperation } from '../useServerOperation';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn() },
  sseUrl: (p: string) => p,
}));
const mocked = api as jest.Mocked<typeof api>;

let es: any;
beforeEach(() => {
  jest.clearAllMocks();
  es = undefined;
  (global as any).EventSource = class {
    onmessage: any = null; onerror: any = null; close = jest.fn();
    constructor(public url: string) { es = this; }
  };
});
const emit = (p: object) => act(() => { es.onmessage({ data: JSON.stringify(p) }); });

function Harness() {
  const op = useServerOperation('s1');
  return (
    <div>
      <button onClick={() => op.start('bootstrap')}>go</button>
      <button onClick={op.clear}>clear</button>
      <span data-testid="running">{String(op.running)}</span>
      <span data-testid="action">{op.action ?? ''}</span>
      <span data-testid="outcome">{op.outcome ?? ''}</span>
      {op.lines.map(l => <i key={l.id} data-kind={l.kind}>{l.text}</i>)}
    </div>
  );
}

describe('useServerOperation', () => {
  it('starts an op, streams lines, and finishes ok', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<Harness />);
    fireEvent.click(screen.getByText('go'));
    await waitFor(() => expect(es).toBeDefined());
    expect(mocked.post).toHaveBeenCalledWith('/api/servers/s1/bootstrap', {});
    expect(es.url).toBe('/api/servers/s1/ssh/ops/o1/stream');
    expect(screen.getByTestId('running').textContent).toBe('true');
    expect(screen.getByTestId('action').textContent).toBe('bootstrap');
    emit({ type: 'log', data: { line: 'hello' } });
    expect(screen.getByText('hello')).toBeInTheDocument();
    emit({ type: 'done', data: { exitCode: 0, message: 'Server is ready.' } });
    expect(screen.getByText('✓ Server is ready.').getAttribute('data-kind')).toBe('ok');
    expect(screen.getByTestId('running').textContent).toBe('false');
    expect(screen.getByTestId('outcome').textContent).toBe('ok');
  });

  it('marks failure and keeps lines', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<Harness />);
    fireEvent.click(screen.getByText('go'));
    await waitFor(() => expect(es).toBeDefined());
    emit({ type: 'done', data: { exitCode: 1, message: 'nope' } });
    expect(screen.getByText('✗ nope').getAttribute('data-kind')).toBe('error');
    expect(screen.getByTestId('outcome').textContent).toBe('error');
  });

  it('reports a start failure', async () => {
    mocked.post.mockRejectedValue({ message: 'No SSH key configured' });
    render(<Harness />);
    fireEvent.click(screen.getByText('go'));
    expect(await screen.findByText('✗ No SSH key configured')).toBeInTheDocument();
    expect(screen.getByTestId('running').textContent).toBe('false');
  });

  it('reports lost connection', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<Harness />);
    fireEvent.click(screen.getByText('go'));
    await waitFor(() => expect(es).toBeDefined());
    act(() => es.onerror());
    expect(screen.getByText('✗ Connection lost')).toBeInTheDocument();
  });

  it('caps stored lines', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<Harness />);
    fireEvent.click(screen.getByText('go'));
    await waitFor(() => expect(es).toBeDefined());
    act(() => {
      for (let i = 0; i < 5100; i++) es.onmessage({ data: JSON.stringify({ type: 'log', data: { line: `l${i}` } }) });
    });
    expect(screen.queryByText('l0')).toBeNull();
    expect(screen.getByText('l5099')).toBeInTheDocument();
  });

  it('clear empties lines', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<Harness />);
    fireEvent.click(screen.getByText('go'));
    await waitFor(() => expect(es).toBeDefined());
    emit({ type: 'log', data: { line: 'x' } });
    fireEvent.click(screen.getByText('clear'));
    expect(screen.queryByText('x')).toBeNull();
  });
});

describe('useServerOperation — rage clicking', () => {
  it('starting twice in the same tick posts once', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<Harness />);
    const go = screen.getByText('go');
    act(() => { fireEvent.click(go); fireEvent.click(go); fireEvent.click(go); });
    await waitFor(() => expect(es).toBeDefined());
    expect(mocked.post).toHaveBeenCalledTimes(1);
  });
});
