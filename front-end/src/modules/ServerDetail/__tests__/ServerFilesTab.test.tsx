import { render, screen, fireEvent, waitFor, within, act } from '@testing-library/react';
import React from 'react';
import ServerFilesTab from '../ServerFilesTab';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), getRaw: jest.fn(), delete: jest.fn(), upload: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));

const mocked = api as jest.Mocked<typeof api>;
const SERVER: any = { id: 's1', name: 'Prod', host: 'h', username: 'ec2-user', sshKeyPath: '/k', remoteWorkingDir: '/home/ec2-user/ship-right' };

const listing = (path: string, entries: any[], parent: string | null = '/home/ec2-user') =>
  ({ path, parent, entries });
const ENTRIES = [
  { name: 'logs', path: '/home/ec2-user/ship-right/logs', isDirectory: true, size: 0, modified: null },
  { name: 'docker-compose.yml', path: '/home/ec2-user/ship-right/docker-compose.yml', isDirectory: false, size: 2048, modified: '2026-10-22T05:00:00Z' },
];

beforeEach(() => {
  jest.clearAllMocks();
  mocked.get.mockImplementation(async (url: string) => {
    if (url.includes('path=%2Fhome%2Fec2-user%2Fship-right%2Flogs')) return listing('/home/ec2-user/ship-right/logs', [], '/home/ec2-user/ship-right') as any;
    return listing('/home/ec2-user/ship-right', ENTRIES) as any;
  });
});

describe('ServerFilesTab', () => {
  it('lists the working directory by default', async () => {
    render(<ServerFilesTab server={SERVER} />);
    expect(await screen.findByText('docker-compose.yml')).toBeInTheDocument();
    expect(screen.getByText('logs')).toBeInTheDocument();
    expect(screen.getByText('2.0 KB')).toBeInTheDocument();
    expect(mocked.get).toHaveBeenCalledWith('/api/servers/s1/files?showHidden=false');
  });

  it('opens a folder on click, and goes up with Up', async () => {
    render(<ServerFilesTab server={SERVER} />);
    fireEvent.click(await screen.findByText('logs'));
    await waitFor(() => expect(mocked.get).toHaveBeenCalledWith(
      '/api/servers/s1/files?path=%2Fhome%2Fec2-user%2Fship-right%2Flogs&showHidden=false'));
    expect(await screen.findByText('This folder is empty.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Up' }));
    await waitFor(() => expect(mocked.get).toHaveBeenLastCalledWith(
      '/api/servers/s1/files?path=%2Fhome%2Fec2-user%2Fship-right&showHidden=false'));
  });

  it('shows hidden files when toggled', async () => {
    render(<ServerFilesTab server={SERVER} />);
    await screen.findByText('logs');
    fireEvent.click(screen.getByLabelText('Show hidden files'));
    await waitFor(() => expect(mocked.get).toHaveBeenLastCalledWith(
      expect.stringContaining('showHidden=true')));
  });

  it('opens a text file in a viewer', async () => {
    mocked.getRaw.mockResolvedValue('services:\n  app: {}');
    render(<ServerFilesTab server={SERVER} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Open docker-compose.yml' }));
    expect(await screen.findByText(/services:/)).toBeInTheDocument();
    expect(mocked.getRaw).toHaveBeenCalledWith(
      '/api/servers/s1/files/read?path=%2Fhome%2Fec2-user%2Fship-right%2Fdocker-compose.yml');
  });

  it('shows why a file could not be opened', async () => {
    mocked.getRaw.mockRejectedValue({ message: 'This looks like a binary file. Download it instead.' });
    render(<ServerFilesTab server={SERVER} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Open docker-compose.yml' }));
    expect(await screen.findByText(/binary file/)).toBeInTheDocument();
  });

  it('has a download link per row; folders download as a compressed archive', async () => {
    render(<ServerFilesTab server={SERVER} />);
    const file = await screen.findByRole('link', { name: 'Download docker-compose.yml' });
    expect(file.getAttribute('href')).toBe(
      '/api/servers/s1/files/download?path=%2Fhome%2Fec2-user%2Fship-right%2Fdocker-compose.yml');
    const folder = screen.getByRole('link', { name: 'Download logs as .tar.gz' });
    expect(folder.getAttribute('href')).toContain('files/download?path=');
  });

  it('uploads into the current folder, then refreshes', async () => {
    mocked.upload.mockResolvedValue({} as any);
    render(<ServerFilesTab server={SERVER} />);
    await screen.findByText('logs');
    const file = new File(['abc'], 'new.txt');
    fireEvent.change(screen.getByLabelText('Upload file'), { target: { files: [file] } });
    await waitFor(() => expect(mocked.upload).toHaveBeenCalledWith(
      '/api/servers/s1/files/upload?path=%2Fhome%2Fec2-user%2Fship-right&name=new.txt', file));
    await waitFor(() => expect(mocked.get.mock.calls.length).toBeGreaterThan(1));
  });

  it('delete needs an explicit confirmation, and Cancel does nothing', async () => {
    render(<ServerFilesTab server={SERVER} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Delete docker-compose.yml' }));
    expect(mocked.delete).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel delete' }));
    expect(mocked.delete).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Confirm delete' })).toBeNull();
  });

  it('confirmed delete calls the API and refreshes', async () => {
    mocked.delete.mockResolvedValue({} as any);
    render(<ServerFilesTab server={SERVER} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Delete docker-compose.yml' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm delete' }));
    await waitFor(() => expect(mocked.delete).toHaveBeenCalledWith(
      '/api/servers/s1/files?path=%2Fhome%2Fec2-user%2Fship-right%2Fdocker-compose.yml'));
  });

  it('shortcuts jump to the working dir and the ShipRight log folder', async () => {
    render(<ServerFilesTab server={SERVER} />);
    await screen.findByText('logs');
    fireEvent.click(screen.getByRole('button', { name: 'Server logs' }));
    await waitFor(() => expect(mocked.get).toHaveBeenLastCalledWith(
      '/api/servers/s1/files?path=%2Fhome%2Fec2-user%2F.shipright&showHidden=false'));
  });

  it('shows a listing error', async () => {
    mocked.get.mockRejectedValue({ message: 'Permission denied on the server.' });
    render(<ServerFilesTab server={SERVER} />);
    expect(await screen.findByText('Permission denied on the server.')).toBeInTheDocument();
  });

  it('breadcrumbs navigate to ancestors', async () => {
    render(<ServerFilesTab server={SERVER} />);
    await screen.findByText('logs');
    const crumbs = screen.getByRole('navigation', { name: 'Path' });
    fireEvent.click(within(crumbs).getByRole('button', { name: 'ec2-user' }));
    await waitFor(() => expect(mocked.get).toHaveBeenLastCalledWith(
      '/api/servers/s1/files?path=%2Fhome%2Fec2-user&showHidden=false'));
  });
});

describe('ServerFilesTab — busy states and rage-click protection', () => {
  const pending = () => {
    let resolve!: (v: any) => void; let reject!: (e: any) => void;
    const promise = new Promise<any>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
  };

  it('shows a loading indicator on first load', async () => {
    const d = pending();
    mocked.get.mockReturnValue(d.promise);
    render(<ServerFilesTab server={SERVER} />);
    expect(await screen.findByRole('status')).toHaveTextContent(/Loading/);
    await act(async () => { d.resolve(listing('/home/ec2-user/ship-right', ENTRIES)); });
    expect(await screen.findByText('docker-compose.yml')).toBeInTheDocument();
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('disables every control while a folder loads, then re-enables them', async () => {
    render(<ServerFilesTab server={SERVER} />);
    await screen.findByText('logs');
    const d = pending();
    mocked.get.mockReturnValueOnce(d.promise);
    fireEvent.click(screen.getByText('logs'));

    expect(await screen.findByRole('status')).toHaveTextContent(/Loading/);
    ['Working dir', 'Server logs', 'Up', 'Refresh'].forEach(n =>
      expect(screen.getByRole('button', { name: n })).toBeDisabled());
    expect(screen.getByLabelText('Show hidden files')).toBeDisabled();
    expect(screen.getByLabelText('Upload file')).toBeDisabled();

    await act(async () => { d.resolve(listing('/home/ec2-user/ship-right/logs', [], '/home/ec2-user/ship-right')); });
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled();
  });

  it('rage-clicking a folder loads it only once', async () => {
    render(<ServerFilesTab server={SERVER} />);
    const folder = await screen.findByText('logs');
    const before = mocked.get.mock.calls.length;
    const d = pending();
    mocked.get.mockReturnValueOnce(d.promise);
    act(() => { fireEvent.click(folder); fireEvent.click(folder); fireEvent.click(folder); });
    expect(mocked.get.mock.calls.length).toBe(before + 1);
    await act(async () => { d.resolve(listing('/home/ec2-user/ship-right/logs', [], '/home/ec2-user/ship-right')); });
  });

  it('opening a file shows a spinner and fires one request however often it is clicked', async () => {
    const d = pending();
    mocked.getRaw.mockReturnValue(d.promise);
    render(<ServerFilesTab server={SERVER} />);
    const open = await screen.findByRole('button', { name: 'Open docker-compose.yml' });
    act(() => { fireEvent.click(open); fireEvent.click(open); fireEvent.click(open); });
    expect(mocked.getRaw).toHaveBeenCalledTimes(1);
    expect(await screen.findByRole('status')).toHaveTextContent(/Opening docker-compose.yml/);
    await act(async () => { d.resolve('hello'); });
    expect(await screen.findByText('hello')).toBeInTheDocument();
  });

  it('confirming a delete several times deletes once, and shows progress', async () => {
    const d = pending();
    mocked.delete.mockReturnValue(d.promise);
    render(<ServerFilesTab server={SERVER} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Delete docker-compose.yml' }));
    const confirm = screen.getByRole('button', { name: 'Confirm delete' });
    act(() => { fireEvent.click(confirm); fireEvent.click(confirm); fireEvent.click(confirm); });
    expect(mocked.delete).toHaveBeenCalledTimes(1);
    expect(await screen.findByRole('status')).toHaveTextContent(/Deleting docker-compose.yml/);
    await act(async () => { d.resolve({}); });
  });

  it('shows the file name while uploading and ignores a second pick', async () => {
    const d = pending();
    mocked.upload.mockReturnValue(d.promise);
    render(<ServerFilesTab server={SERVER} />);
    await screen.findByText('logs');
    const input = screen.getByLabelText('Upload file');
    const file = new File(['abc'], 'big.zip');
    act(() => {
      fireEvent.change(input, { target: { files: [file] } });
      fireEvent.change(input, { target: { files: [file] } });
    });
    expect(mocked.upload).toHaveBeenCalledTimes(1);
    expect(await screen.findByRole('status')).toHaveTextContent(/Uploading big.zip/);
    await act(async () => { d.resolve({}); });
  });

  it('a second click on a download link is swallowed while the first is preparing', async () => {
    render(<ServerFilesTab server={SERVER} />);
    const link = await screen.findByRole('link', { name: 'Download logs as .tar.gz' });
    fireEvent.click(link);
    expect(await screen.findByText(/Preparing download/)).toBeInTheDocument();
    expect(fireEvent.click(link)).toBe(false); // preventDefault → no second archive job
  });
});
