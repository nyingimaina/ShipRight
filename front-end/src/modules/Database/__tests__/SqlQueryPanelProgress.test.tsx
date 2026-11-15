import { render, screen, fireEvent, waitFor, act, within } from '@testing-library/react';
import SqlQueryPanel from '../SqlQueryPanel';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), getRaw: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('react-syntax-highlighter', () => ({ Prism: ({ children }: { children: string }) => <pre data-testid="preview">{children}</pre> }));
jest.mock('react-syntax-highlighter/dist/esm/styles/prism', () => ({ vscDarkPlus: {} }));
jest.mock('jattac.libs.web.responsive-table', () => ({ __esModule: true, default: () => <div /> }));
jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: ({ onSelect }: { onSelect: (p: string) => void }) => (
    <button type="button" onClick={() => onSelect('C:\\dumps\\big-dump.sql')}>pick big-dump.sql</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  static CLOSED = 2;
  onmessage: ((e: { data: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  readyState = 1;
  constructor(public url: string) { FakeEventSource.instances.push(this); }
  close() { this.readyState = 2; }
  emit(type: string, data: Record<string, unknown>) { act(() => { this.onmessage?.({ data: JSON.stringify({ type, data }) }); }); }
}

beforeEach(() => {
  jest.clearAllMocks();
  FakeEventSource.instances = [];
  (global as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
  window.HTMLElement.prototype.scrollIntoView = jest.fn();
});

async function runFile() {
  mockApi.getRaw.mockResolvedValueOnce('SELECT 1;');
  mockApi.post.mockResolvedValueOnce({ opId: 'op1' } as never);
  render(<SqlQueryPanel apiBase="/api/projects/p1/db" />);
  fireEvent.click(screen.getByRole('button', { name: 'From file' }));
  fireEvent.click(screen.getByRole('button', { name: 'pick big-dump.sql' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Execute' }));
  await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
  return FakeEventSource.instances[0];
}

describe('the file name stays in view', () => {
  it('is shown while previewing, while running and when it is done', async () => {
    mockApi.getRaw.mockResolvedValueOnce('SELECT 1;');
    mockApi.post.mockResolvedValueOnce({ opId: 'op1' } as never);
    render(<SqlQueryPanel apiBase="/api/projects/p1/db" />);
    fireEvent.click(screen.getByRole('button', { name: 'From file' }));
    fireEvent.click(screen.getByRole('button', { name: 'pick big-dump.sql' }));
    expect(await screen.findByText('big-dump.sql')).toBeInTheDocument();             // preview

    fireEvent.click(screen.getByRole('button', { name: 'Execute' }));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    expect(screen.getByText('big-dump.sql')).toBeInTheDocument();                     // running

    FakeEventSource.instances[0].emit('complete', { status: 'success' });
    expect(await screen.findByText('Query executed successfully.')).toBeInTheDocument();
    expect(screen.getByText('big-dump.sql')).toBeInTheDocument();                     // done
  });

  it('has no file name when the SQL was typed in', async () => {
    mockApi.post.mockResolvedValueOnce({ opId: 'op2' } as never);
    render(<SqlQueryPanel apiBase="/api/projects/p1/db" />);
    fireEvent.change(screen.getByPlaceholderText(/select \* from users/i), { target: { value: 'SELECT 1;' } });
    fireEvent.click(screen.getByRole('button', { name: 'Execute' }));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    expect(screen.queryByText('big-dump.sql')).not.toBeInTheDocument();
  });
});

describe('progress while the file is uploaded and run', () => {
  it('shows a progress bar that follows the upload, then the run', async () => {
    const es = await runFile();
    es.emit('progress', { stage: 'upload', percent: 38, doneBytes: 5 * 1024 * 1024, totalBytes: 13 * 1024 * 1024 });

    const bar = await screen.findByRole('progressbar', { name: /uploading/i });
    expect(bar).toHaveAttribute('aria-valuenow', '38');
    expect(screen.getByText(/38%/)).toBeInTheDocument();
    expect(screen.getByText(/5\.0 MB of 13\.0 MB/)).toBeInTheDocument();

    es.emit('progress', { stage: 'run', percent: 61, doneBytes: 8 * 1024 * 1024, totalBytes: 13 * 1024 * 1024 });
    const run = await screen.findByRole('progressbar', { name: /running/i });
    expect(run).toHaveAttribute('aria-valuenow', '61');
    expect(screen.queryByRole('progressbar', { name: /uploading/i })).not.toBeInTheDocument();
  });

  it('keeps the log lines as they arrive', async () => {
    const es = await runFile();
    es.emit('log', { message: 'Uploading… 38% (5.0 MB of 13.0 MB, 2.5 MB/s)' });
    es.emit('log', { message: 'Running… 61% (8.0 MB of 13.0 MB)' });
    const lines = await screen.findAllByText(/MB of 13\.0 MB/);
    expect(lines.length).toBeGreaterThanOrEqual(2);
  });

  it('takes the bar away when the run finishes', async () => {
    const es = await runFile();
    es.emit('progress', { stage: 'run', percent: 99, doneBytes: 1, totalBytes: 1 });
    expect(await screen.findByRole('progressbar')).toBeInTheDocument();
    es.emit('complete', { status: 'success' });
    await screen.findByText('Query executed successfully.');
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('takes the bar away when it fails, and shows the reason', async () => {
    const es = await runFile();
    es.emit('progress', { stage: 'run', percent: 40, doneBytes: 1, totalBytes: 2 });
    es.emit('error', { message: 'Query command exited with code 1.' });
    expect(await screen.findByText('Query command exited with code 1.')).toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('starts from nothing for the next run', async () => {
    const es = await runFile();
    es.emit('progress', { stage: 'upload', percent: 50, doneBytes: 1, totalBytes: 2 });
    es.emit('complete', { status: 'success' });
    await screen.findByText('Query executed successfully.');
    fireEvent.click(screen.getByRole('button', { name: /new query|run another|change file|reset|back/i }));
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(within(document.body).queryByText(/50%/)).not.toBeInTheDocument();
  });
});
