import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import KeyFilesPanel from '../KeyFilesPanel';
import ResourcesPage from '@/pages/resources';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn() },
}));

jest.mock('react-hot-toast', () => ({
  __esModule: true,
  default: { success: jest.fn(), error: jest.fn() },
}));

jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));

jest.mock('@/modules/AppShell/AppShell', () => ({ __esModule: true, default: ({ children }: any) => <div>{children}</div> }));

jest.mock('jattac.libs.web.zest-responsive-layout', () => ({
  ZestResponsiveLayout: ({ children, sidePane }: any) => (
    <div>{children}{sidePane?.visible && <aside>{sidePane.content}</aside>}</div>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => <input value={value} onChange={onChange} placeholder={placeholder} />,
}));

jest.mock('jattac.libs.web.overflow-menu', () => ({
  __esModule: true,
  default: ({ items }: any) => (
    <div>
      {items?.map((item: any) => (
        <span key={item.content} role="button" onClick={item.onClick}>{item.content}</span>
      ))}
    </div>
  ),
}));

jest.mock('jattac.libs.web.zest-file-upload', () => ({
  __esModule: true,
  default: () => null,
}));

const mockGet = api.get as jest.Mock;
const mockDelete = api.delete as jest.Mock;

const keyFile = (over: Partial<any> = {}) => ({
  id: '1',
  name: 'ec2-prod',
  tags: ['aws'],
  createdAt: '2024-01-01T00:00:00Z',
  modifiedAt: '2024-02-01T00:00:00Z',
  ...over,
});

describe('KeyFilesPanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockGet.mockReset();
    mockDelete.mockReset();
  });

  it('R4 shows a loading state before the first page arrives, then the key files', async () => {
    let resolveGet: (value: any) => void = () => {};
    mockGet.mockReturnValue(new Promise(resolve => { resolveGet = resolve; }));

    render(<KeyFilesPanel />);
    expect(screen.getByText('Loading key files…')).toBeInTheDocument();

    await act(async () => {
      resolveGet({ items: [keyFile()], total: 1, page: 1, pageSize: 20 });
    });

    expect(await screen.findByText('ec2-prod')).toBeInTheDocument();
  });

  it('R4 shows an explicit empty state that explains what a key file is', async () => {
    mockGet.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 });

    render(<KeyFilesPanel />);

    expect(await screen.findByText(/No key files yet/)).toBeInTheDocument();
  });

  it('R4 shows an error state distinct from empty, with a working retry', async () => {
    mockGet
      .mockRejectedValueOnce({ message: 'Key files could not be loaded.' })
      .mockResolvedValueOnce({ items: [], total: 0, page: 1, pageSize: 20 });

    render(<KeyFilesPanel />);

    expect(await screen.findByText('Key files could not be loaded.')).toBeInTheDocument();
    expect(screen.queryByText(/No key files yet/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('Retry'));

    expect(await screen.findByText(/No key files yet/)).toBeInTheDocument();
    expect(mockGet).toHaveBeenCalledTimes(2);
  });

  it('R4 renders a very long key-file name without breaking the card', async () => {
    const long = 'x'.repeat(300);
    mockGet.mockResolvedValue({ items: [keyFile({ name: long })], total: 1, page: 1, pageSize: 20 });

    render(<KeyFilesPanel />);

    expect(await screen.findByText(long)).toBeInTheDocument();
  });

  it('R7 asks for confirmation and deletes nothing when cancelled', async () => {
    mockGet.mockResolvedValue({ items: [keyFile()], total: 1, page: 1, pageSize: 20 });
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);

    render(<KeyFilesPanel />);
    await screen.findByText('ec2-prod');
    fireEvent.click(screen.getByText('Delete'));

    expect(confirm).toHaveBeenCalled();
    expect(mockDelete).not.toHaveBeenCalled();
    expect(screen.getByText('ec2-prod')).toBeInTheDocument();
  });

  it('R7 deletes after confirmation and removes the row', async () => {
    mockGet.mockResolvedValue({ items: [keyFile()], total: 1, page: 1, pageSize: 20 });
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    mockDelete.mockResolvedValue({ message: 'Key file deleted.' });

    render(<KeyFilesPanel />);
    await screen.findByText('ec2-prod');
    fireEvent.click(screen.getByText('Delete'));

    await waitFor(() => {
      expect(mockDelete).toHaveBeenCalledWith('/api/resources/key-files/1');
    });
    await waitFor(() => {
      expect(screen.queryByText('ec2-prod')).not.toBeInTheDocument();
    });
  });

  it('R9 pages one page at a time', async () => {
    mockGet.mockResolvedValue({ items: [keyFile()], total: 25, page: 1, pageSize: 20 });

    render(<KeyFilesPanel />);
    await screen.findByText('ec2-prod');

    expect(screen.getByText('Page 1 of 2')).toBeInTheDocument();
    expect(mockGet).toHaveBeenCalledWith('/api/resources/key-files?page=1&pageSize=20');

    fireEvent.click(screen.getByText('Next'));

    await waitFor(() => {
      expect(mockGet).toHaveBeenLastCalledWith('/api/resources/key-files?page=2&pageSize=20');
    });
  });
});

// R4 edge case: the Key files tab fetches its data only when the user opens it, so tabs the user never
// visits do no work (QA-3).
describe('Resources page — Key files tab is lazy', () => {
  const keyFileCalls = () =>
    mockGet.mock.calls.filter(([url]) => String(url).startsWith('/api/resources/key-files'));

  beforeEach(() => {
    jest.clearAllMocks();
    mockGet.mockReset();
    mockGet.mockResolvedValue([]);
  });

  it('R4 does not fetch key files until the Key files tab is opened', async () => {
    render(<ResourcesPage />);

    await waitFor(() => expect(mockGet).toHaveBeenCalledWith('/api/resources/registries'));
    expect(keyFileCalls()).toHaveLength(0);

    fireEvent.click(screen.getByText('Key Files'));

    await waitFor(() => expect(keyFileCalls().length).toBeGreaterThan(0));
    expect(keyFileCalls()[0][0]).toBe('/api/resources/key-files?page=1&pageSize=20');
  });
});
