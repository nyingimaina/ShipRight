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

  it('closes from the Cancel button', () => {
    const onClose = jest.fn();
    render(<ImportProjectDialog onClose={onClose} onImported={jest.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalled();
  });
});
