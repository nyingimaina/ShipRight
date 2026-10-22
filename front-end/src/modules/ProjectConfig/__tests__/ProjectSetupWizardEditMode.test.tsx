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
  services: [],
  gitRepos: [],
  wsl: { workingDir: '' },
  server: { host: 'h', username: 'u', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: '', deployMode: 'GitScript' },
  timeZone: 'UTC',
  createdAt: '',
  modifiedAt: '',
};

beforeEach(() => {
  jest.resetAllMocks();
  mockApi.get.mockResolvedValue([] as any);
});

describe('editing an existing project', () => {
  it('is titled as the project settings, not as a detection review', async () => {
    render(<ProjectSetupWizard existing={stored} onSaved={jest.fn()} onCancel={jest.fn()} />);

    expect(await screen.findByText('Project settings')).toBeTruthy();
    expect(screen.queryByText('Review detected config')).toBeNull();
    expect(screen.getByText(/change anything below, then press save/i)).toBeTruthy();
  });

  it('does not show the create-wizard step dots, since there are no earlier steps to go back to', async () => {
    render(<ProjectSetupWizard existing={stored} onSaved={jest.fn()} onCancel={jest.fn()} />);

    await screen.findByText('Project settings');
    expect(screen.queryByTestId('wizard-steps')).toBeNull();
  });

  it('still reaches the server settings and comes back with the Back button, without the dots', async () => {
    render(<ProjectSetupWizard existing={stored} onSaved={jest.fn()} onCancel={jest.fn()} />);

    fireEvent.click(await screen.findByText('Server config →'));
    expect(await screen.findByText('Server')).toBeTruthy();
    expect(screen.queryByTestId('wizard-steps')).toBeNull();

    fireEvent.click(screen.getByText('← Back'));
    expect(await screen.findByText('Project settings')).toBeTruthy();
  });
});

describe('creating a new project', () => {
  it('still shows the step dots once past the first step', async () => {
    render(<ProjectSetupWizard onSaved={jest.fn()} onCancel={jest.fn()} />);
    await waitFor(() => expect(mockApi.get).toHaveBeenCalled());

    fireEvent.click(screen.getByText('Freeform'));
    fireEvent.click(screen.getByText('Continue'));

    expect(await screen.findByTestId('wizard-steps')).toBeTruthy();
  });
});
