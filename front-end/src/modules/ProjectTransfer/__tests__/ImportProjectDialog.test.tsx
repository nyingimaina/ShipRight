import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import ImportProjectDialog from '../ImportProjectDialog';
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
  counts: { servers: 1, registries: 2, services: 1 },
  warnings: [
    { severity: 'warning', code: 'path', location: "service 'api'", message: "service 'api': build context is 'D:\work\api'" },
    { severity: 'warning', code: 'secret-omitted', location: "credential 'pat'", message: "credential 'pat': the value is never exported." },
    { severity: 'info', code: 'version-differs', location: 'bundle', message: 'Exported from ShipRight 4.4.14; this is 5.0.0.' },
  ],
  ...over,
});

const file = new File(['zip'], 'app.zip', { type: 'application/zip' });
const choose = () => fireEvent.change(screen.getByLabelText('Export file'), { target: { files: [file] } });

describe('ImportProjectDialog', () => {
  beforeEach(() => jest.clearAllMocks());

  it('inspects the chosen file and shows what is inside and what to check', async () => {
    upload.mockResolvedValue(inspection());
    render(<ImportProjectDialog onClose={jest.fn()} onImported={jest.fn()} />);
    choose();

    await waitFor(() => expect(screen.getByText('App')).toBeInTheDocument());
    expect(upload).toHaveBeenCalledWith('/api/projects/import/inspect', file);
    expect(screen.getByText(/build context is 'D:\work\api'/)).toBeInTheDocument();
    expect(screen.getByText(/the value is never exported/)).toBeInTheDocument();
    expect(screen.getByText(/this is 5.0.0/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Import project' })).toBeEnabled();
  });

  it('cannot import a bundle the server says is not importable, and says why', async () => {
    upload.mockResolvedValue(inspection({ importable: false, errors: ['This bundle was made by a newer ShipRight.'] }));
    render(<ImportProjectDialog onClose={jest.fn()} onImported={jest.fn()} />);
    choose();

    await waitFor(() => expect(screen.getByText(/made by a newer ShipRight/)).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Import project' })).toBeDisabled();
  });

  it('shows the server message when the file is not a ShipRight export', async () => {
    upload.mockRejectedValue({ isError: true, message: 'This is not a ShipRight export.' });
    render(<ImportProjectDialog onClose={jest.fn()} onImported={jest.fn()} />);
    choose();
    await waitFor(() => expect(screen.getByText('This is not a ShipRight export.')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Import project' })).toBeNull();
  });

  it('imports on confirm, tells the parent, and repeats the things to fix', async () => {
    upload.mockResolvedValueOnce(inspection());
    upload.mockResolvedValueOnce({ projectId: 'app-imported', projectName: 'App (imported)', warnings: inspection().warnings });
    const onImported = jest.fn();
    render(<ImportProjectDialog onClose={jest.fn()} onImported={onImported} />);
    choose();
    await waitFor(() => screen.getByRole('button', { name: 'Import project' }));

    fireEvent.click(screen.getByRole('button', { name: 'Import project' }));

    await waitFor(() => expect(screen.getByText(/App \(imported\)/)).toBeInTheDocument());
    expect(upload).toHaveBeenLastCalledWith('/api/projects/import', file);
    expect(onImported).toHaveBeenCalledWith(expect.objectContaining({ projectId: 'app-imported' }));
    expect(screen.getByText(/build context is 'D:\work\api'/)).toBeInTheDocument();
  });

  it('uses the passphrase for both inspection and import of a protected bundle', async () => {
    upload.mockResolvedValueOnce(inspection({
      secretsIncluded: true,
      warnings: [{ severity: 'warning', code: 'secrets-included', location: 'bundle', message: 'Contains live secrets.' }],
    }));
    upload.mockResolvedValueOnce({ projectId: 'app-2', projectName: 'App 2', warnings: [] });
    render(<ImportProjectDialog onClose={jest.fn()} onImported={jest.fn()} />);

    fireEvent.change(screen.getByLabelText('Bundle passphrase'), { target: { value: 's3cret' } });
    choose();

    await waitFor(() => expect(screen.getByText('Contains live secrets.')).toBeInTheDocument());
    expect(upload).toHaveBeenCalledWith('/api/projects/import/inspect', file, { 'X-ShipRight-Passphrase': 's3cret' });

    fireEvent.click(screen.getByRole('button', { name: 'Import project' }));
    await waitFor(() => expect(upload).toHaveBeenLastCalledWith('/api/projects/import', file, { 'X-ShipRight-Passphrase': 's3cret' }));
  });

  it('asks for a destination root when the export had machine-specific paths', async () => {
    upload.mockResolvedValueOnce(inspection({
      requiredInputs: ['project.services[0].buildContextPath'],
      relocatedPaths: ['{ProjectRoot}/api'],
    }));
    upload.mockResolvedValueOnce({ projectId: 'app-3', projectName: 'App 3', warnings: [] });
    render(<ImportProjectDialog onClose={jest.fn()} onImported={jest.fn()} />);

    choose();
    const root = await screen.findByLabelText('Destination project root');
    fireEvent.change(root, { target: { value: '/opt/app' } });
    fireEvent.click(screen.getByRole('button', { name: 'Import project' }));

    await waitFor(() => expect(upload).toHaveBeenLastCalledWith('/api/projects/import', file, {
      'X-ShipRight-Target-Root': '/opt/app',
    }));
  });

  it('closes from the Cancel button', () => {
    const onClose = jest.fn();
    render(<ImportProjectDialog onClose={onClose} onImported={jest.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
  });
});
