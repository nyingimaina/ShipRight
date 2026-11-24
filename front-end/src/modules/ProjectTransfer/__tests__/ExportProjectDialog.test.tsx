import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import ExportProjectDialog from '../ExportProjectDialog';
import { exportProject } from '../exportProject';

jest.mock('../exportProject', () => ({ exportProject: jest.fn() }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled, zest }: any) => (
    <button type="button" data-semantic={zest?.semanticType} onClick={(e: any) => { void Promise.resolve(onClick(e)).catch(() => undefined); }} disabled={disabled}>{children}</button>
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

  it('marks the export button with export semantics so Zest can report busy, success and failure', () => {
    renderDialog();
    fireEvent.change(screen.getByLabelText('Passphrase'), { target: { value: 'long-passphrase' } });
    fireEvent.change(screen.getByLabelText('Confirm passphrase'), { target: { value: 'long-passphrase' } });
    fireEvent.click(screen.getByLabelText(/I understand/));

    expect(screen.getByRole('button', { name: 'Export with secrets' })).toHaveAttribute('data-semantic', 'export');
  });

  it('shows the real export failure as an alert and scrolls to it after a pause', async () => {
    jest.useFakeTimers();
    const scrollIntoView = jest.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    exportMock.mockRejectedValueOnce(new Error('Export rejected. Check logs for request ID abc.'));
    renderDialog();
    fireEvent.change(screen.getByLabelText('Passphrase'), { target: { value: 'long-passphrase' } });
    fireEvent.change(screen.getByLabelText('Confirm passphrase'), { target: { value: 'long-passphrase' } });
    fireEvent.click(screen.getByLabelText(/I understand/));
    fireEvent.click(screen.getByRole('button', { name: 'Export with secrets' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Export rejected. Check logs for request ID abc.');
    expect(scrollIntoView).not.toHaveBeenCalled();
    jest.advanceTimersByTime(250);
    expect(scrollIntoView).toHaveBeenCalledWith({ behavior: 'smooth', block: 'nearest' });
    jest.useRealTimers();
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
