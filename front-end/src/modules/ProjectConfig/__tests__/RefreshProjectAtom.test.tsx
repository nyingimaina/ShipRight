import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import RefreshProjectAtom from '../atoms/RefreshProjectAtom';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { post: jest.fn() },
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

const change = {
  serviceName: 'Lattice Services Foundation',
  oldVersionFilePath: 'D:\\r\\S\\P\\version.txt',
  newVersionFilePath: 'D:\\r\\S\\P\\version.txt',
  oldBuildContextPath: 'D:\\r\\S\\P',
  newBuildContextPath: 'D:\\r\\S',
};

const planWith = (changes: any[], extra: any = {}) => ({
  plan: { changes, notFoundInFolder: [], newInFolder: [], ...extra },
  project: null,
});

function setup(onApplied = jest.fn()) {
  render(<RefreshProjectAtom projectId="lattice" defaultRootPath={'D:\\r'} onApplied={onApplied} />);
  return onApplied;
}

describe('RefreshProjectAtom', () => {
  beforeEach(() => mockApi.post.mockReset());

  it('starts with the folder filled in from the project and offers no Apply yet', () => {
    setup();

    expect((screen.getByPlaceholderText(/root folder/i) as HTMLInputElement).value).toBe('D:\\r');
    expect(screen.queryByText(/apply/i)).toBeNull();
  });

  it('checks the folder without saving, then lists exactly what would change', async () => {
    mockApi.post.mockResolvedValueOnce(planWith([change]));
    setup();

    fireEvent.click(screen.getByText('Check folder'));

    await waitFor(() => expect(screen.getByText(/Lattice Services Foundation/)).toBeTruthy());
    expect(mockApi.post).toHaveBeenCalledWith('/api/projects/lattice/refresh-paths/preview', { rootPath: 'D:\\r' });
    expect(screen.getByText(/D:\\r\\S\\P$/)).toBeTruthy();
    expect(screen.getByText(/D:\\r\\S$/)).toBeTruthy();
    expect(screen.getByText('Apply 1 change')).toBeTruthy();
  });

  it('uses the folder as edited when checking', async () => {
    mockApi.post.mockResolvedValueOnce(planWith([]));
    setup();

    fireEvent.change(screen.getByPlaceholderText(/root folder/i), { target: { value: 'D:\\other' } });
    fireEvent.click(screen.getByText('Check folder'));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalled());
    expect(mockApi.post).toHaveBeenCalledWith('/api/projects/lattice/refresh-paths/preview', { rootPath: 'D:\\other' });
  });

  it('says everything is up to date, and offers no Apply, when nothing would change', async () => {
    mockApi.post.mockResolvedValueOnce(planWith([]));
    setup();

    fireEvent.click(screen.getByText('Check folder'));

    await waitFor(() => expect(screen.getByText(/already up to date/i)).toBeTruthy());
    expect(screen.queryByText(/^Apply/)).toBeNull();
  });

  it('applies on the server, then hands the applied changes to the form', async () => {
    mockApi.post
      .mockResolvedValueOnce(planWith([change]))
      .mockResolvedValueOnce(planWith([change]));
    const onApplied = setup();

    fireEvent.click(screen.getByText('Check folder'));
    await waitFor(() => screen.getByText('Apply 1 change'));
    fireEvent.click(screen.getByText('Apply 1 change'));

    await waitFor(() => expect(onApplied).toHaveBeenCalledWith([change]));
    expect(mockApi.post).toHaveBeenLastCalledWith('/api/projects/lattice/refresh-paths/apply', { rootPath: 'D:\\r' });
    expect(screen.getByText(/paths updated and saved/i)).toBeTruthy();
  });

  it('mentions services it could not match, and never offers to add or remove them', async () => {
    mockApi.post.mockResolvedValueOnce(planWith([], { notFoundInFolder: ['Old One'], newInFolder: ['Brand New'] }));
    setup();

    fireEvent.click(screen.getByText('Check folder'));

    await waitFor(() => expect(screen.getByText(/Old One/)).toBeTruthy());
    expect(screen.getByText(/Brand New/)).toBeTruthy();
    expect(screen.queryByText(/^Apply/)).toBeNull();
  });

  it('shows the server message when the check fails and changes nothing', async () => {
    mockApi.post.mockRejectedValueOnce({ isError: true, message: 'Directory not found: D:\\nope' });
    const onApplied = setup();

    fireEvent.click(screen.getByText('Check folder'));

    await waitFor(() => expect(screen.getByText(/Directory not found/)).toBeTruthy());
    expect(onApplied).not.toHaveBeenCalled();
    expect(screen.queryByText(/^Apply/)).toBeNull();
  });

  it('keeps the form untouched when applying fails', async () => {
    mockApi.post
      .mockResolvedValueOnce(planWith([change]))
      .mockRejectedValueOnce({ isError: true, message: 'Could not save' });
    const onApplied = setup();

    fireEvent.click(screen.getByText('Check folder'));
    await waitFor(() => screen.getByText('Apply 1 change'));
    fireEvent.click(screen.getByText('Apply 1 change'));

    await waitFor(() => expect(screen.getByText(/Could not save/)).toBeTruthy());
    expect(onApplied).not.toHaveBeenCalled();
  });

  it('discards an old preview when the folder is edited, so Apply cannot run on stale results', async () => {
    mockApi.post.mockResolvedValueOnce(planWith([change]));
    setup();

    fireEvent.click(screen.getByText('Check folder'));
    await waitFor(() => screen.getByText('Apply 1 change'));
    fireEvent.change(screen.getByPlaceholderText(/root folder/i), { target: { value: 'D:\\elsewhere' } });

    expect(screen.queryByText(/^Apply/)).toBeNull();
  });
});
