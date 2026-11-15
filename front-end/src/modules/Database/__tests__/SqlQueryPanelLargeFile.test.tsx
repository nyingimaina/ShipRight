import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import SqlQueryPanel from '../SqlQueryPanel';
import { formatBytes } from '../formatBytes';
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
    <div>
      <div>file picker</div>
      <button type="button" onClick={() => onSelect('/data/big-dump.sql')}>pick big-dump.sql</button>
    </div>
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

const THIRTEEN_MB = 13 * 1024 * 1024;
const TOO_LARGE = { isError: true, tooLarge: true, sizeBytes: THIRTEEN_MB, limitBytes: 512 * 1024, message: 'File too large to preview (13,631,488 bytes; limit is 512 KB).' };

beforeEach(() => {
  jest.clearAllMocks();
  FakeEventSource.instances = [];
  (global as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
  window.HTMLElement.prototype.scrollIntoView = jest.fn();   // jsdom does not implement it
});

async function pickFile() {
  render(<SqlQueryPanel apiBase="/api/projects/p1/db" />);
  fireEvent.click(screen.getByRole('button', { name: 'From file' }));
  fireEvent.click(screen.getByRole('button', { name: 'pick big-dump.sql' }));
}

describe('formatBytes', () => {
  it('uses the unit a person would use', () => {
    expect(formatBytes(900)).toBe('900 bytes');
    expect(formatBytes(2048)).toBe('2.0 KB');
    expect(formatBytes(13 * 1024 * 1024)).toBe('13.0 MB');
    expect(formatBytes(5.5 * 1024 * 1024 * 1024)).toBe('5.5 GB');
  });
});

describe('a script too big to preview', () => {
  it('asks whether to run it without a preview, saying what it is and how big', async () => {
    mockApi.getRaw.mockRejectedValueOnce(TOO_LARGE);
    await pickFile();

    expect(await screen.findByText('big-dump.sql')).toBeInTheDocument();
    expect(screen.getByText(/13\.0 MB/)).toBeInTheDocument();
    expect(screen.getByText(/too large to preview/i)).toBeInTheDocument();
    expect(screen.getByText(/run it without a preview\?/i)).toBeInTheDocument();
    expect(screen.queryByText(/limit is 512 KB/)).not.toBeInTheDocument();   // not left as a raw error
    expect(mockApi.post).not.toHaveBeenCalled();                              // nothing runs until they say so
  });

  it('runs the whole file on the server once they confirm', async () => {
    mockApi.getRaw.mockRejectedValueOnce(TOO_LARGE);
    mockApi.post.mockResolvedValueOnce({ opId: 'op1' } as never);
    await pickFile();

    fireEvent.click(await screen.findByRole('button', { name: /run without preview/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/projects/p1/db/query', { localSqlPath: '/data/big-dump.sql' }));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    expect(FakeEventSource.instances[0].url).toBe('/api/projects/p1/db/ops/op1/stream');

    FakeEventSource.instances[0].emit('complete', { status: 'success' });
    expect(await screen.findByText('Query executed successfully.')).toBeInTheDocument();
  });

  it('lets them choose another file instead', async () => {
    mockApi.getRaw.mockRejectedValueOnce(TOO_LARGE);
    await pickFile();
    fireEvent.click(await screen.findByRole('button', { name: /choose another file/i }));
    expect(await screen.findByText('file picker')).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('forgets the big file when they change file, so a normal file previews as usual', async () => {
    mockApi.getRaw.mockRejectedValueOnce(TOO_LARGE).mockResolvedValueOnce('SELECT 1;');
    await pickFile();
    fireEvent.click(await screen.findByRole('button', { name: /choose another file/i }));
    fireEvent.click(await screen.findByRole('button', { name: 'From file' }));
    fireEvent.click(screen.getByRole('button', { name: 'pick big-dump.sql' }));
    expect(await screen.findByTestId('preview')).toHaveTextContent('SELECT 1;');
    expect(screen.queryByText(/too large to preview/i)).not.toBeInTheDocument();
  });
});

describe('other problems reading the file', () => {
  it('are still shown as errors, with no offer to run it blind', async () => {
    mockApi.getRaw.mockRejectedValueOnce({ isError: true, message: 'Access denied.' });
    await pickFile();
    expect(await screen.findByText('Access denied.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /run without preview/i })).not.toBeInTheDocument();
  });
});
