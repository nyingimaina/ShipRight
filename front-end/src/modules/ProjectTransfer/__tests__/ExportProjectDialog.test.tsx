import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import ExportProjectDialog from '../ExportProjectDialog';
import { exportProject } from '../exportProject';

jest.mock('../exportProject', () => ({ exportProject: jest.fn() }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const exportMock = exportProject as jest.Mock;

describe('ExportProjectDialog', () => {
  beforeEach(() => exportMock.mockReset());

  const renderDialog = () => render(
    <ExportProjectDialog projectId="p1" projectName="My app" onClose={jest.fn()} onExported={jest.fn()} />,
  );

  it('requires an explicit risk acknowledgement before exporting secrets', async () => {
    exportMock.mockResolvedValue(undefined);
    renderDialog();

    const button = screen.getByRole('button', { name: 'Export with secrets' });
    expect(button).toBeDisabled();
    expect(screen.getByText(/I understand that this export contains live secrets/i)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Passphrase'), { target: { value: 'long-passphrase' } });
    fireEvent.change(screen.getByLabelText('Confirm passphrase'), { target: { value: 'long-passphrase' } });
    expect(button).toBeDisabled();

    fireEvent.click(screen.getByLabelText(/I understand/));
    expect(button).toBeEnabled();

    fireEvent.click(button);
    await waitFor(() => expect(exportMock).toHaveBeenCalledWith('p1', {
      includeSecrets: true, passphrase: 'long-passphrase',
    }));
  });

  it('refuses mismatched passphrases', () => {
    renderDialog();
    fireEvent.change(screen.getByLabelText('Passphrase'), { target: { value: 'one' } });
    fireEvent.change(screen.getByLabelText('Confirm passphrase'), { target: { value: 'two' } });
    fireEvent.click(screen.getByLabelText(/I understand/));
    expect(screen.getByRole('button', { name: 'Export with secrets' })).toBeDisabled();
    expect(screen.getByText(/do not match/i)).toBeInTheDocument();
  });
});
