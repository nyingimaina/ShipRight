import { render, screen, act, fireEvent } from '@testing-library/react';
import ProjectSetupWizard from '../ProjectSetupWizard';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn(),
    post: jest.fn(),
    put: jest.fn(),
  },
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

function setupFreeform() {
  render(<ProjectSetupWizard onSaved={jest.fn()} onCancel={jest.fn()} />);
  // choose Freeform mode
  fireEvent.click(screen.getByText('Freeform'));
  return screen.getByText('Features');
}

function toggleFeature(label: string) {
  const checkbox = screen.getByLabelText(new RegExp(label, 'i'));
  fireEvent.click(checkbox);
}

describe('ProjectSetupWizard freeform', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.get.mockResolvedValue([]);
  });

  it('freeform save posts type Freeform with persisted feature flags', async () => {
    setupFreeform();
    // default features: docker=true, git=true, deploy=true, database=false
    toggleFeature('Docker build');
    toggleFeature('Git repositories');
    // leave deploy selected, database unchecked

    // Deploy selected routes to the server step, which exposes the project name
    // field and the Create Project button.
    mockApi.post.mockResolvedValue({ id: 'x', name: 'My App' });

    fireEvent.click(screen.getByText('Continue'));

    const nameInputs = screen.getAllByPlaceholderText('e.g. SMS Gateway');
    fireEvent.change(nameInputs[0], { target: { value: 'My App' } });
    fireEvent.click(screen.getByText('Create Project'));

    await act(async () => {});

    expect(mockApi.post).toHaveBeenCalledWith('/api/projects', expect.objectContaining({
      name: 'My App',
      type: 'Freeform',
      features: { docker: false, git: false, deploy: true, database: false },
    }));
  });

  it('freeform with only git selected routes to source pick, not minimal save', () => {
    setupFreeform();
    toggleFeature('Docker build');
    toggleFeature('Deploy server');
    // keep git selected, leave database unchecked

    fireEvent.click(screen.getByText('Continue'));

    expect(screen.getByText('Where is your project?')).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });
});
