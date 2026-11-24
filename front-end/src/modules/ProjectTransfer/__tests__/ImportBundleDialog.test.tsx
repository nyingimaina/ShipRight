import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import ImportBundleDialog from '../ImportBundleDialog';
import styles from '../ImportBundleDialog.module.css';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { upload: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));
const upload = (api as any).upload as jest.Mock;

const inspection = (over: object = {}) => ({
  projectName: 'App', importable: true, errors: [],
  counts: { servers: 1 },
  warnings: [],
  ...over,
});

const file = new File(['zip'], 'app.shrsec', { type: 'application/octet-stream' });
const choose = () => fireEvent.change(screen.getByLabelText('Export file'), { target: { files: [file] } });
const passphrase = () => screen.getByLabelText('Bundle passphrase');
const read = () => fireEvent.click(screen.getByRole('button', { name: 'Read bundle' }));

describe('ImportBundleDialog', () => {
  beforeEach(() => jest.clearAllMocks());

  it('uses a styled Choose file control instead of the raw browser file button', () => {
    render(<ImportBundleDialog onClose={jest.fn()} onImported={jest.fn()} />);

    expect(screen.getByText('Choose file')).toBeInTheDocument();
    expect(screen.getByLabelText('Export file')).toHaveClass(styles.hiddenFile);
  });

  it('accepts a passphrase after a protected file was chosen and then reads and imports it', async () => {
    upload.mockRejectedValueOnce({ isError: true, message: 'This export is encrypted. Enter its passphrase to inspect or import it.' });
    upload.mockResolvedValueOnce(inspection({ secretsIncluded: true }));
    upload.mockResolvedValueOnce({ projectId: 'app-2', projectName: 'App 2', warnings: [] });
    render(<ImportBundleDialog onClose={jest.fn()} onImported={jest.fn()} />);

    choose();
    await waitFor(() => expect(screen.getByText(/encrypted/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Import project' })).toBeNull();

    fireEvent.change(passphrase(), { target: { value: 's3cret' } });
    expect(screen.queryByText(/encrypted/)).toBeNull();

    read();
    await waitFor(() => expect(screen.getByText('App')).toBeInTheDocument());
    expect(upload).toHaveBeenCalledWith('/api/projects/import/inspect', file, { 'X-ShipRight-Passphrase': 's3cret' });

    fireEvent.click(screen.getByRole('button', { name: 'Import project' }));
    await waitFor(() => expect(upload).toHaveBeenLastCalledWith('/api/projects/import', file, { 'X-ShipRight-Passphrase': 's3cret' }));
  });

  it('does not reuse an inspection after the passphrase changes', async () => {
    upload.mockResolvedValue(inspection());
    render(<ImportBundleDialog onClose={jest.fn()} onImported={jest.fn()} />);

    choose();
    await waitFor(() => expect(screen.getByText('App')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Import project' })).toBeEnabled();

    fireEvent.change(passphrase(), { target: { value: 'new-passphrase' } });
    expect(screen.queryByText('App')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Import project' })).toBeNull();

    read();
    await waitFor(() => expect(screen.getByText('App')).toBeInTheDocument());
    expect(upload).toHaveBeenLastCalledWith('/api/projects/import/inspect', file, { 'X-ShipRight-Passphrase': 'new-passphrase' });
  });

  it('still reads an ordinary zip as soon as it is chosen', async () => {
    upload.mockResolvedValue(inspection());
    render(<ImportBundleDialog onClose={jest.fn()} onImported={jest.fn()} />);

    choose();
    await waitFor(() => expect(screen.getByText('App')).toBeInTheDocument());
    expect(upload).toHaveBeenCalledWith('/api/projects/import/inspect', file);
  });

  it('imports a protected bundle with a destination root', async () => {
    upload.mockResolvedValueOnce(inspection({ requiredInputs: ['project.services[0].buildContextPath'] }));
    upload.mockResolvedValueOnce({ projectId: 'app-3', projectName: 'App 3', warnings: [] });
    render(<ImportBundleDialog onClose={jest.fn()} onImported={jest.fn()} />);

    choose();
    fireEvent.change(passphrase(), { target: { value: 's3cret' } });
    read();
    const root = await screen.findByLabelText('Destination project root');
    fireEvent.change(root, { target: { value: '/opt/app' } });
    fireEvent.click(screen.getByRole('button', { name: 'Import project' }));

    await waitFor(() => expect(upload).toHaveBeenLastCalledWith('/api/projects/import', file, {
      'X-ShipRight-Passphrase': 's3cret',
      'X-ShipRight-Target-Root': '/opt/app',
    }));
  });
});
