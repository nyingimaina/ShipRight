import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import React from 'react';
import RemoteTerminal from '../RemoteTerminal';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { post: jest.fn() },
  sseUrl: (p: string) => p,
}));

class FakeES {
  static last: FakeES;
  onmessage: ((e: { data: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  closed = false;
  constructor(public url: string) { FakeES.last = this; }
  close() { this.closed = true; }
  emit(type: string, data: object) { this.onmessage?.({ data: JSON.stringify({ type, data }) }); }
}
(global as any).EventSource = FakeES;

const mocked = api as jest.Mocked<typeof api>;
const box = () => screen.getByLabelText('Command') as HTMLInputElement;

beforeEach(() => {
  jest.clearAllMocks();
  localStorage.clear();
  mocked.post.mockResolvedValue({ opId: 'op1' } as any);
});

describe('RemoteTerminal', () => {
  it('uses the server endpoints for a server and the project endpoints for a project', async () => {
    const { unmount } = render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.change(box(), { target: { value: 'ls' } });
    fireEvent.keyDown(box(), { key: 'Enter' });
    await waitFor(() => expect(mocked.post).toHaveBeenCalledWith('/api/servers/s1/ssh/exec', { command: 'ls' }));
    expect(FakeES.last.url).toBe('/api/servers/s1/ssh/ops/op1/stream');
    unmount();

    render(<RemoteTerminal projectId="p1" serverLabel="u@h" />);
    fireEvent.change(box(), { target: { value: 'ls' } });
    fireEvent.keyDown(box(), { key: 'Enter' });
    await waitFor(() => expect(mocked.post).toHaveBeenCalledWith('/api/projects/p1/ssh/exec', { command: 'ls' }));
  });

  it('echoes the command and streams its output, then shows the exit status', async () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.change(box(), { target: { value: 'free -m' } });
    fireEvent.keyDown(box(), { key: 'Enter' });
    await waitFor(() => expect(FakeES.last).toBeDefined());
    act(() => {
      FakeES.last.emit('log', { source: 'stdout', line: 'Mem: 900' });
      FakeES.last.emit('done', { exitCode: 0, cwd: '/home/u' });
    });
    expect(screen.getByText('$ free -m')).toBeInTheDocument();
    expect(screen.getByText('Mem: 900')).toBeInTheDocument();
    expect(screen.getByText('✓ exit 0')).toBeInTheDocument();
    expect(screen.getByText('/home/u')).toBeInTheDocument();
  });

  it('flags a non-zero exit', async () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.change(box(), { target: { value: 'false' } });
    fireEvent.keyDown(box(), { key: 'Enter' });
    await waitFor(() => expect(FakeES.last).toBeDefined());
    act(() => { FakeES.last.emit('done', { exitCode: 2, cwd: '~' }); });
    expect(screen.getByText('✗ exit 2')).toBeInTheDocument();
  });

  it('ignores Enter while a command is running (no double submit)', async () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.change(box(), { target: { value: 'sleep 5' } });
    fireEvent.keyDown(box(), { key: 'Enter' });
    fireEvent.keyDown(box(), { key: 'Enter' });
    await waitFor(() => expect(mocked.post).toHaveBeenCalledTimes(1));
    expect(box()).toBeDisabled();
  });

  it('a pasted multi-line script is run as one chained command', async () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.paste(box(), { clipboardData: { getData: () => 'cd /srv\nls\n' } });
    expect(box().value).toBe('cd /srv && ls');
  });

  it('offers ready-made diagnostic commands that fill the box without running them', () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.click(screen.getByRole('button', { name: 'Database logs' }));
    expect(box().value).toBe('docker logs --tail 40 shipright-db');
    expect(mocked.post).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Database crash reason' }));
    expect(box().value).toBe("docker inspect shipright-db -f '{{.State.ExitCode}} oom={{.State.OOMKilled}}'");
    fireEvent.click(screen.getByRole('button', { name: 'Memory and disk' }));
    expect(box().value).toBe('free -m; df -h /');
  });

  it('the one-click database check chains logs, crash reason and memory', () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.click(screen.getByRole('button', { name: 'Why is the database down?' }));
    expect(box().value).toBe(
      "docker logs --tail 40 shipright-db; docker inspect shipright-db -f '{{.State.ExitCode}} oom={{.State.OOMKilled}}'; free -m");
  });

  it('presets can reference the compose folder', () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" startDir="/srv/app" />);
    fireEvent.click(screen.getByRole('button', { name: 'Images to deploy' }));
    expect(box().value).toBe("cd '/srv/app' && docker compose config --images");
  });

  it('hides the presets that need a folder when none is known', () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    expect(screen.queryByRole('button', { name: 'Images to deploy' })).toBeNull();
  });

  it('remembers history across mounts and recalls it with the arrow keys', async () => {
    const { unmount } = render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.change(box(), { target: { value: 'uptime' } });
    fireEvent.keyDown(box(), { key: 'Enter' });
    await waitFor(() => expect(FakeES.last).toBeDefined());
    act(() => { FakeES.last.emit('done', { exitCode: 0, cwd: '~' }); });
    unmount();

    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.keyDown(box(), { key: 'ArrowUp' });
    expect(box().value).toBe('uptime');
  });

  it('shows a clear error when the command cannot be started', async () => {
    mocked.post.mockRejectedValue(new Error('No host'));
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    fireEvent.change(box(), { target: { value: 'ls' } });
    fireEvent.keyDown(box(), { key: 'Enter' });
    expect(await screen.findByText('✗ No host')).toBeInTheDocument();
    expect(box()).not.toBeDisabled();
  });

  it('has the shared output toolbar (copy / download / filter)', () => {
    render(<RemoteTerminal serverId="s1" serverLabel="u@h" />);
    expect(screen.getByRole('button', { name: 'Copy all' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download log' })).toBeInTheDocument();
    expect(screen.getByLabelText('Filter log')).toBeInTheDocument();
  });
});
