import { render, screen, act, fireEvent, waitFor } from '@testing-library/react';
import React from 'react';
import PipelinesPage from '../../pages/pipelines';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn(),
    post: jest.fn(),
    put: jest.fn(),
    delete: jest.fn(),
  },
}));

jest.mock('next/head', () => ({
  __esModule: true,
  default: ({ children }: any) => <>{children}</>,
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled, zest }: any) => (
    <button onClick={onClick} disabled={disabled} data-variant={zest?.visualOptions?.variant}>
      {children}
    </button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

jest.mock('jattac.libs.web.overflow-menu', () => ({
  __esModule: true,
  default: () => <div data-testid="overflow-menu" />,
}));

jest.mock('../../modules/AppShell/AppShell', () => ({
  __esModule: true,
  default: ({ children }: any) => <div>{children}</div>,
}));

// Capture the PipelineBuilder props to simulate the flow
let lastBuilderProps: any = null;
jest.mock('../../modules/BuildWizard/PipelineBuilder', () => ({
  __esModule: true,
  default: (props: any) => {
    lastBuilderProps = props;
    return (
      <div data-testid="pipeline-builder">
        <button onClick={props.onCreateProject}>+ New Project</button>
        <button onClick={() => props.onSave({ id: 'p1', name: 'P', steps: [], scope: 'Project', projectId: props.initialProjectId })}>
          save
        </button>
        <span data-testid="initial-project">{props.initialProjectId || ''}</span>
      </div>
    );
  },
}));

let lastWizardProps: any = null;
jest.mock('../../modules/ProjectConfig/ProjectSetupWizard', () => ({
  __esModule: true,
  default: (props: any) => {
    lastWizardProps = props;
    return (
      <div data-testid="project-wizard">
        <button onClick={() => props.onSaved({ id: 'new-proj', name: 'New Proj', services: [], gitRepos: [], wsl: { workingDir: '' }, server: { host: '', username: '', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: '', deployMode: 'GitScript' }, createdAt: '', modifiedAt: '' })}>
          save project
        </button>
        <button onClick={props.onCancel}>cancel project</button>
      </div>
    );
  },
}));

jest.mock('../../modules/BuildWizard/BuildWizard', () => ({
  __esModule: true,
  default: ({ projectId }: any) => <div data-testid="build-wizard" data-project={projectId} />,
}));

jest.mock('jattac.libs.web.zest-responsive-layout', () => ({
  __esModule: true,
  ZestResponsiveLayout: ({ children, sidePane }: any) => (
    <div data-testid="layout">
      {children}
      {sidePane?.visible && (
        <div data-testid="side-pane" data-title={sidePane.title}>
          {sidePane.content}
          <button onClick={sidePane.onClose}>close pane</button>
        </div>
      )}
    </div>
  ),
}));

jest.mock('../../pages/Styles/Pipelines.module.css', () => ({}));

const mockApi = api as jest.Mocked<typeof api>;

const emptyProject = (id: string, name: string) => ({
  id, name, services: [], gitRepos: [], wsl: { workingDir: '' },
  server: { host: '', username: '', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: '', deployMode: 'GitScript' as const },
  createdAt: '', modifiedAt: '',
});

describe('PipelinesPage inline project creation', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    lastBuilderProps = null;
    lastWizardProps = null;
    mockApi.get.mockImplementation((url: string) => {
      if (url.includes('/api/resources/pipelines')) return Promise.resolve([]);
      if (url.includes('/api/projects')) return Promise.resolve([emptyProject('proj-1', 'Existing Project')]);
      return Promise.resolve(null);
    });
    mockApi.post.mockResolvedValue(emptyProject('new-proj', 'New Proj'));
  });

  it('renders the pipelines page', async () => {
    render(<PipelinesPage />);
    await waitFor(() => {
      expect(screen.getByText('Pipelines')).toBeInTheDocument();
    });
  });

  it('opens PipelineBuilder in new mode', async () => {
    render(<PipelinesPage />);
    await waitFor(() => expect(screen.getByText('Pipelines')).toBeInTheDocument());
    fireEvent.click(screen.getByText('New Pipeline'));
    expect(screen.getByTestId('pipeline-builder')).toBeInTheDocument();
  });

  it('switches to ProjectSetupWizard when + New Project is clicked from the builder', async () => {
    render(<PipelinesPage />);
    await waitFor(() => expect(screen.getByText('Pipelines')).toBeInTheDocument());
    fireEvent.click(screen.getByText('New Pipeline'));
    fireEvent.click(screen.getByText('+ New Project'));
    expect(screen.getByTestId('project-wizard')).toBeInTheDocument();
  });

  it('returns to PipelineBuilder with initialProjectId after creating a project', async () => {
    render(<PipelinesPage />);
    await waitFor(() => expect(screen.getByText('Pipelines')).toBeInTheDocument());
    fireEvent.click(screen.getByText('New Pipeline'));

    // Open project wizard
    fireEvent.click(screen.getByText('+ New Project'));
    expect(screen.getByTestId('project-wizard')).toBeInTheDocument();

    // Save the project
    await act(async () => {
      fireEvent.click(screen.getByText('save project'));
    });

    // Back in the builder with initialProjectId set
    await waitFor(() => {
      expect(screen.getByTestId('pipeline-builder')).toBeInTheDocument();
    });
    expect(screen.getByTestId('initial-project').textContent).toBe('new-proj');
  });

  it('canceling the project wizard returns to the pipeline builder', async () => {
    render(<PipelinesPage />);
    await waitFor(() => expect(screen.getByText('Pipelines')).toBeInTheDocument());
    fireEvent.click(screen.getByText('New Pipeline'));
    fireEvent.click(screen.getByText('+ New Project'));
    expect(screen.getByTestId('project-wizard')).toBeInTheDocument();
    fireEvent.click(screen.getByText('cancel project'));
    expect(screen.getByTestId('pipeline-builder')).toBeInTheDocument();
    expect(screen.getByTestId('initial-project').textContent).toBe('');
  });

  it('creates a project from the global pipeline picker and proceeds to build', async () => {
    mockApi.get.mockImplementation((url: string) => {
      if (url.includes('/api/resources/pipelines')) return Promise.resolve([
        { id: 'g1', name: 'Global Pipe', steps: [], scope: 'Global' },
      ]);
      if (url.includes('/api/projects')) return Promise.resolve([emptyProject('proj-1', 'Existing Project')]);
      if (url.includes('/current-versions')) return Promise.resolve([]);
      return Promise.resolve(null);
    });

    render(<PipelinesPage />);
    await waitFor(() => expect(screen.getByText('Global Pipe')).toBeInTheDocument());

    // Click Build on the global pipeline — opens project picker
    fireEvent.click(screen.getByText('Build'));
    await waitFor(() => {
      expect(screen.getByText('Select Project')).toBeInTheDocument();
    });
    expect(screen.getByText('+ New Project')).toBeInTheDocument();

    // Open the project wizard from the picker
    fireEvent.click(screen.getByText('+ New Project'));
    expect(screen.getByTestId('project-wizard')).toBeInTheDocument();

    // Save the project
    await act(async () => {
      fireEvent.click(screen.getByText('save project'));
    });

    // Proceeds to the BuildWizard with the new project
    await waitFor(() => {
      expect(screen.getByTestId('build-wizard')).toBeInTheDocument();
    });
    expect(screen.getByTestId('build-wizard').getAttribute('data-project')).toBe('new-proj');
  });
});
