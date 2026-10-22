import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import ProjectSetupWizard from '../ProjectSetupWizard';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn() },
}));

jest.mock('next/link', () => {
  const MockLink = ({ children }: any) => <span>{children}</span>;
  MockLink.displayName = 'MockLink';
  return MockLink;
});

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

jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: () => <div>FilePicker</div>,
}));

const mockApi = api as jest.Mocked<typeof api>;

const stored: any = {
  id: 'lattice',
  name: 'Lattice',
  type: 'Pipeline',
  serverId: 'srv-1',
  services: [{
    name: 'Lattice Services Foundation',
    versionFilePath: 'D:\\r\\S\\P\\version.txt',
    buildContextPath: 'D:\\r\\S\\P',
    dockerImageName: 'nyingi/lattice-foundation',
    dockerRegistry: 'docker.io',
    dockerRegistryResourceId: 'registry-resource-1',
    composeServiceName: 'lattice',
    dockerUsername: 'registry-user',
    imageRetentionCount: 7,
    localImageKeepCount: 3,
  }],
  gitRepos: [{ repoPath: 'D:\\r', deployBranch: 'master' }],
  wsl: { workingDir: '/home/x/docker' },
  server: { host: 'h', username: 'u', sshKeyPath: '/k', remoteWorkingDir: '/app', rebuildScript: 'r.sh', deployMode: 'GitScript' },
  timeZone: 'Africa/Nairobi',
  createdAt: '2026-01-01T00:00:00Z',
  modifiedAt: '2026-01-01T00:00:00Z',
};

const change = {
  serviceName: 'Lattice Services Foundation',
  oldVersionFilePath: 'D:\\r\\S\\P\\version.txt',
  newVersionFilePath: 'D:\\r\\S\\P\\version.txt',
  oldBuildContextPath: 'D:\\r\\S\\P',
  newBuildContextPath: 'D:\\r\\S',
};

beforeEach(() => {
  jest.resetAllMocks();
  mockApi.get.mockResolvedValue([] as any);
});

describe('ProjectSetupWizard refresh', () => {
  it('offers the refresh when editing an existing project', async () => {
    render(<ProjectSetupWizard existing={stored} onSaved={jest.fn()} onCancel={jest.fn()} />);

    expect(await screen.findByText('Refresh paths from folder')).toBeTruthy();
  });

  it('does not offer it while creating a new project', async () => {
    render(<ProjectSetupWizard onSaved={jest.fn()} onCancel={jest.fn()} />);

    await waitFor(() => expect(mockApi.get).toHaveBeenCalled());
    expect(screen.queryByText('Refresh paths from folder')).toBeNull();
  });

  it('after a refresh is applied, a later Save sends the new path and does not revert it', async () => {
    mockApi.post
      .mockResolvedValueOnce({ plan: { changes: [change], notFoundInFolder: [], newInFolder: [] }, project: null } as any)
      .mockResolvedValueOnce({ plan: { changes: [change], notFoundInFolder: [], newInFolder: [] }, project: stored } as any);
    mockApi.put.mockResolvedValue(stored as any);
    render(<ProjectSetupWizard existing={stored} onSaved={jest.fn()} onCancel={jest.fn()} />);

    fireEvent.click(await screen.findByText('Check folder'));
    fireEvent.click(await screen.findByText('Apply 1 change'));
    await screen.findByText(/paths updated and saved/i);
    fireEvent.click(screen.getByText('Save'));

    await waitFor(() => expect(mockApi.put).toHaveBeenCalled());
    const sent: any = mockApi.put.mock.calls[0][1];
    expect(sent.services[0].buildContextPath).toBe('D:\\r\\S');
    expect(sent.services[0].name).toBe('Lattice Services Foundation');
  });

  it('a plain Save keeps the link to the saved registry the service already had', async () => {
    mockApi.put.mockResolvedValue(stored as any);
    render(<ProjectSetupWizard existing={stored} onSaved={jest.fn()} onCancel={jest.fn()} />);

    fireEvent.click(await screen.findByText('Save'));

    await waitFor(() => expect(mockApi.put).toHaveBeenCalled());
    const sent: any = mockApi.put.mock.calls[0][1];
    expect(sent.services[0].dockerRegistryResourceId).toBe('registry-resource-1');
    expect(sent.services[0].imageRetentionCount).toBe(7);
    expect(sent.services[0].localImageKeepCount).toBe(3);
  });
});
