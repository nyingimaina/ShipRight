import { render, screen, act, fireEvent, waitFor } from '@testing-library/react';
import React from 'react';
import PipelineBuilder from '../PipelineBuilder';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn().mockImplementation((url: string) => {
      if (url.includes('/api/resources/scripts')) return Promise.resolve([]);
      if (url.includes('/api/projects')) return Promise.resolve([
        { id: 'proj-1', name: 'Existing Project' },
      ]);
      return Promise.resolve([]);
    }),
    post: jest.fn().mockResolvedValue({}),
    put: jest.fn().mockResolvedValue({}),
  },
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled, zest }: any) => (
    <button onClick={onClick} disabled={disabled} data-semantic={zest?.semanticType}>
      {children}
    </button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: () => <input />,
}));

jest.mock('@dnd-kit/core', () => ({
  DndContext: ({ children }: any) => <div>{children}</div>,
  closestCenter: jest.fn(),
  KeyboardSensor: jest.fn(),
  PointerSensor: jest.fn(),
  useSensor: jest.fn(() => ({})),
  useSensors: jest.fn(() => []),
}));

jest.mock('@dnd-kit/sortable', () => ({
  SortableContext: ({ children }: any) => <div>{children}</div>,
  sortableKeyboardCoordinates: jest.fn(),
  verticalListSortingStrategy: jest.fn(),
  useSortable: jest.fn(() => ({
    attributes: {},
    listeners: {},
    setNodeRef: jest.fn(),
    transform: null,
    transition: null,
    isDragging: false,
  })),
}));

jest.mock('@dnd-kit/utilities', () => ({
  CSS: { Transform: { toString: () => '' } },
}));

jest.mock('react-hot-toast', () => ({
  __esModule: true,
  default: { error: jest.fn(), success: jest.fn() },
}));

jest.mock('../Styles/PipelineBuilder.module.css', () => ({}));

const mockApi = api as jest.Mocked<typeof api>;

describe('PipelineBuilder', () => {
  const defaultProps = {
    onSave: jest.fn(),
    onCancel: jest.fn(),
  };

  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.get.mockImplementation((url: string) => {
      if (url.includes('/api/resources/scripts')) return Promise.resolve([]);
      if (url.includes('/api/projects')) return Promise.resolve([
        { id: 'proj-1', name: 'Existing Project' },
      ]);
      return Promise.resolve([]);
    });
    mockApi.post.mockResolvedValue({});
    mockApi.put.mockResolvedValue({});
  });

  it('renders pipeline name input and scope selector', () => {
    render(<PipelineBuilder {...defaultProps} />);
    expect(screen.getByPlaceholderText('Pipeline name...')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Global')).toBeInTheDocument();
  });

  it('shows project dropdown when scope is Project', async () => {
    render(<PipelineBuilder {...defaultProps} />);
    const scopeSelect = screen.getByDisplayValue('Global');
    await act(async () => {
      fireEvent.change(scopeSelect, { target: { value: 'Project' } });
    });
    await waitFor(() => {
      expect(screen.getByText('Existing Project')).toBeInTheDocument();
    });
  });

  it('shows + New Project button when scope is Project', async () => {
    render(<PipelineBuilder {...defaultProps} onCreateProject={jest.fn()} />);
    const scopeSelect = screen.getByDisplayValue('Global');
    await act(async () => {
      fireEvent.change(scopeSelect, { target: { value: 'Project' } });
    });
    await waitFor(() => {
      expect(screen.getByText('+ New Project')).toBeInTheDocument();
    });
  });

  it('calls onCreateProject when + New Project is clicked', async () => {
    const onCreateProject = jest.fn();
    render(<PipelineBuilder {...defaultProps} onCreateProject={onCreateProject} />);
    const scopeSelect = screen.getByDisplayValue('Global');
    await act(async () => {
      fireEvent.change(scopeSelect, { target: { value: 'Project' } });
    });
    await waitFor(() => {
      expect(screen.getByText('+ New Project')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByText('+ New Project'));
    expect(onCreateProject).toHaveBeenCalledTimes(1);
  });

  it('initializes projectId from initialProjectId prop', async () => {
    render(<PipelineBuilder {...defaultProps} initialProjectId="proj-1" />);
    const scopeSelect = screen.getByDisplayValue('Global');
    await act(async () => {
      fireEvent.change(scopeSelect, { target: { value: 'Project' } });
    });
    await waitFor(() => {
      const projectSelect = screen.getByDisplayValue('Existing Project');
      expect(projectSelect).toBeInTheDocument();
    });
  });

  it('does not show + New Project when onCreateProject is not provided', async () => {
    render(<PipelineBuilder {...defaultProps} />);
    const scopeSelect = screen.getByDisplayValue('Global');
    await act(async () => {
      fireEvent.change(scopeSelect, { target: { value: 'Project' } });
    });
    await waitFor(() => {
      expect(screen.getByText('Existing Project')).toBeInTheDocument();
    });
    expect(screen.queryByText('+ New Project')).not.toBeInTheDocument();
  });

  it('saves pipeline with selected project', async () => {
    const onSave = jest.fn();
    const cryptoSpy = jest.spyOn(crypto, 'randomUUID').mockReturnValue('test-uuid' as any);
    const dateSpy = jest.spyOn(Date.prototype, 'toISOString').mockReturnValue('2026-01-01T00:00:00.000Z');

    render(<PipelineBuilder {...defaultProps} onSave={onSave} />);
    const scopeSelect = screen.getByDisplayValue('Global');
    await act(async () => {
      fireEvent.change(scopeSelect, { target: { value: 'Project' } });
    });
    await waitFor(() => {
      expect(screen.getByText('Existing Project')).toBeInTheDocument();
    });

    // Enter pipeline name
    fireEvent.change(screen.getByPlaceholderText('Pipeline name...'), { target: { value: 'Test Pipeline' } });

    // Select project
    const projectSelect = screen.getByDisplayValue('Select project…');
    await act(async () => {
      fireEvent.change(projectSelect, { target: { value: 'proj-1' } });
    });

    // Add a Build step
    await act(async () => {
      fireEvent.click(screen.getByText('+ Build'));
    });

    // Save
    await act(async () => {
      fireEvent.click(screen.getByText('Create Pipeline'));
    });

    await waitFor(() => {
      expect(mockApi.post).toHaveBeenCalledWith('/api/resources/pipelines', expect.objectContaining({
        name: 'Test Pipeline',
        scope: 'Project',
        projectId: 'proj-1',
      }));
      expect(onSave).toHaveBeenCalled();
    });

    cryptoSpy.mockRestore();
    dateSpy.mockRestore();
  });
});
