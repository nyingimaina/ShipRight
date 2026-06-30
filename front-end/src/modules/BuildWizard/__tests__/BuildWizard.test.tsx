import { render, screen, act, fireEvent } from '@testing-library/react';
import BuildWizard from '../BuildWizard';
import { api } from '@/shared/ApiService';
import { buildSse } from '@/shared/SseService';

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn().mockResolvedValue([]),
    post: jest.fn(),
    put: jest.fn(),
    getRaw: jest.fn(),
  },
  sseUrl: jest.fn((url: string) => url),
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
  default: ({ value, onChange, placeholder, zest }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} data-variant={zest?.zSize} />
  ),
}));

jest.mock('../LogViewer', () => ({
  __esModule: true,
  default: ({ lines, isLive }: any) => (
    <div data-testid="log-viewer" data-live={isLive}>
      {lines.map((l: any) => <div key={l.id}>{l.line}</div>)}
    </div>
  ),
}));

jest.mock('../OptionPicker', () => ({
  __esModule: true,
  default: ({ options, value, onChange, onConfirm }: any) => (
    <div data-testid="option-picker">
      {options.map((opt: any) => (
        <button key={opt.value} onClick={() => { onChange(opt.value); onConfirm(opt.value); }}>
          {opt.label}
        </button>
      ))}
    </div>
  ),
}));

jest.mock('canvas-confetti', () => jest.fn());

let sseHandlers: Record<string, any> = {};
jest.mock('@/shared/SseService', () => ({
  buildSse: {
    connect: jest.fn((_id: string, handlers: any) => { sseHandlers = handlers; }),
    disconnect: jest.fn(),
    catchUp: jest.fn().mockResolvedValue(null),
  },
}));

jest.mock('react-hot-toast', () => ({
  __esModule: true,
  default: { error: jest.fn(), success: jest.fn(), loading: jest.fn() },
}));

jest.mock('vaul', () => ({
  Drawer: {
    Root: ({ children, open }: any) => (
      <div data-testid="drawer-root" data-open={open}>{children}</div>
    ),
    Portal: ({ children }: any) => <div data-testid="drawer-portal">{children}</div>,
    Overlay: ({ className }: any) => <div data-testid="drawer-overlay" className={className} />,
    Content: ({ children, className }: any) => (
      <div data-testid="drawer-content" className={className}>{children}</div>
    ),
    Title: ({ children, className }: any) => <div data-testid="drawer-title" className={className}>{children}</div>,
  },
}));

jest.mock('react-dom', () => ({
  ...jest.requireActual('react-dom'),
  createPortal: (node: any) => node,
}));

describe('BuildWizard', () => {
  const defaultProps = {
    projectName: 'Test Project',
    projectId: 'test-project',
    isOpen: true,
    onClose: jest.fn(),
    apiBase: '/api/projects/test-project',
    defaultDeployMode: 'GitScript' as const,
    currentVersions: [{
      serviceName: 'api',
      version: '1.0.0',
      suggestedNext: '1.1.0',
      versionFilePath: '/tmp/v.txt',
      error: null as string | null,
    }],
    steps: [] as string[],
  };

  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('renders without crashing', () => {
    render(<BuildWizard {...defaultProps} />);
    expect(screen.getByTestId('drawer-root')).toBeInTheDocument();
  });

  it('shows version confirmation phase initially', () => {
    render(<BuildWizard {...defaultProps} />);
    expect(screen.getByText('Start Build')).toBeInTheDocument();
  });

  it('renders LogViewer when in pipeline phase', async () => {
    jest.useFakeTimers();
    render(<BuildWizard {...defaultProps} />);
    expect(screen.getByTestId('drawer-content')).toBeInTheDocument();
    jest.useRealTimers();
  });

  it('shows action bar with cancel button during pipeline phase', () => {
    jest.useFakeTimers();
    const { container } = render(<BuildWizard {...defaultProps} />);
    const content = screen.getByTestId('drawer-content');
    expect(content).toBeInTheDocument();
    jest.useRealTimers();
  });

  describe('activeOp / busy state', () => {
    beforeEach(() => {
      jest.clearAllMocks();
      sseHandlers = {};
      (api.post as jest.Mock).mockResolvedValue({ buildId: 'test-123' });
      (api.get as jest.Mock).mockResolvedValue({ id: 'test-123', status: 'Building' });
    });

    it('hides status indicator when idle', () => {
      render(<BuildWizard {...defaultProps} />);
      expect(screen.queryByText('Building...')).not.toBeInTheDocument();
      expect(screen.queryByText('Pushing to registry...')).not.toBeInTheDocument();
      expect(screen.queryByText('Deploying...')).not.toBeInTheDocument();
    });

    it('shows building status indicator during build', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      expect(screen.getByText('Building...')).toBeInTheDocument();
    });

    it('shows push status indicator during push', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      expect(screen.getByText('Pushing to registry...')).toBeInTheDocument();
    });

    it('disables push button during active push', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      expect(screen.getByText('Push to Registry').closest('button')).toBeDisabled();
    });

    it('clears activeOp on push completed', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      expect(screen.getByText('Pushing to registry...')).toBeInTheDocument();
      await act(async () => {
        sseHandlers.onPushCompleted?.({ status: 'PushSucceeded' });
      });
      expect(screen.queryByText('Pushing to registry...')).not.toBeInTheDocument();
    });

    it('shows SSE reconnect warning during active operation', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onConnectionChange?.('reconnecting');
      });
      expect(screen.getByText('Building...')).toBeInTheDocument();
      expect(screen.getByText('(reconnecting...)')).toBeInTheDocument();
    });

    it('clears activeOp on deploy completed', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      await act(async () => {
        sseHandlers.onPushCompleted?.({ status: 'PushSucceeded' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Deploy to Production'));
      });
      expect(screen.getByText('Deploying...')).toBeInTheDocument();
      await act(async () => {
        sseHandlers.onDeployCompleted?.({ status: 'Deployed' });
      });
      expect(screen.queryByText('Deploying...')).not.toBeInTheDocument();
    });

    it('disconnects SSE on build completed terminal status', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      expect(buildSse.disconnect).not.toHaveBeenCalled();
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      expect(buildSse.disconnect).toHaveBeenCalled();
    });

    it('disconnects SSE on push completed', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      expect(buildSse.disconnect).toHaveBeenCalledTimes(1);
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      await act(async () => {
        sseHandlers.onPushCompleted?.({ status: 'PushSucceeded' });
      });
      expect(buildSse.disconnect).toHaveBeenCalledTimes(2);
    });

    it('disconnects SSE on deploy completed', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      await act(async () => {
        sseHandlers.onPushCompleted?.({ status: 'PushSucceeded' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Deploy to Production'));
      });
      await act(async () => {
        sseHandlers.onDeployCompleted?.({ status: 'Deployed' });
      });
      expect(buildSse.disconnect).toHaveBeenCalled();
    });

    it('switches phase to pipeline when push starts so elapsed timer is visible', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      // Timer bar renders when phase === 'pipeline' (even elapsed=0 shows it)
      expect(screen.getByText(/⏱/)).toBeInTheDocument();
    });

    it('switches phase to pipeline when deploy starts so elapsed timer is visible', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      await act(async () => {
        sseHandlers.onPushCompleted?.({ status: 'PushSucceeded' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Deploy to Production'));
      });
      expect(screen.getByText(/⏱/)).toBeInTheDocument();
    });

    it('resets activeOp to idle when build start API call fails', async () => {
      (api.post as jest.Mock).mockRejectedValueOnce({ message: 'Server error' });
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      expect(screen.queryByText('Building...')).not.toBeInTheDocument();
      expect(screen.getByText('Start Build')).toBeInTheDocument();
    });

    it('resets activeOp and push phase when push API call fails', async () => {
      (api.post as jest.Mock)
        .mockResolvedValueOnce({ buildId: 'test-123' })   // build start
        .mockRejectedValueOnce({ message: 'Push error' }); // push start
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {
        fireEvent.click(screen.getByText('Start Build'));
      });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {
        fireEvent.click(screen.getByText('Push to Registry'));
      });
      expect(screen.queryByText('Pushing to registry...')).not.toBeInTheDocument();
    });
  });
});
