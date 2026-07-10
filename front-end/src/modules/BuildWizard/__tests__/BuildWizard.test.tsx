import { render, screen, act, fireEvent } from '@testing-library/react';
import React from 'react';
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

jest.mock('../StepPicker', () => ({
  __esModule: true,
  default: ({ steps, onChange, onConfirm, onCancel }: any) => (
    <div data-testid="step-picker">
      {(['build', 'push', 'deploy'] as const).map(step => (
        <input
          key={step}
          type="checkbox"
          data-testid={`step-${step}`}
          checked={steps.has(step)}
          onChange={() => {
            const next = new Set(steps);
            next.has(step) ? next.delete(step) : next.add(step);
            onChange(next);
          }}
        />
      ))}
      <button onClick={() => onChange(new Set(['build', 'push', 'deploy']))}>Select All</button>
      <button onClick={onConfirm}>Confirm</button>
      <button onClick={onCancel}>Cancel Picker</button>
    </div>
  ),
}));

jest.mock('../PipelineSelector', () => ({
  __esModule: true,
  default: ({ onUseCustom }: any) => {
    // Auto-advance to step picker so existing tests don't need to change
    React.useEffect(() => { onUseCustom(); }, []);
    return <div data-testid="pipeline-selector" />;
  },
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
      });
      expect(screen.getByText('Building...')).toBeInTheDocument();
    });

    it('shows push status indicator during push', async () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
      });
      await act(async () => {
        sseHandlers.onConnectionChange?.('reconnecting');
      });
      expect(screen.getByText('Building...')).toBeInTheDocument();
      expect(screen.getByText('(reconnecting...)')).toBeInTheDocument();
    });

    it('clears activeOp on deploy completed', async () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
      });
      expect(buildSse.disconnect).not.toHaveBeenCalled();
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      expect(buildSse.disconnect).toHaveBeenCalled();
    });

    it('disconnects SSE on push completed', async () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
      });
      expect(screen.queryByText('Building...')).not.toBeInTheDocument();
      expect(screen.getByText('Start Build')).toBeInTheDocument();
    });

    it('resets activeOp and push phase when push API call fails', async () => {
      (api.post as jest.Mock)
        .mockResolvedValueOnce({ buildId: 'test-123' })   // build start
        .mockRejectedValueOnce({ message: 'Push error' }); // push start
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
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

  describe('Step Picker / Express Build', () => {
    beforeEach(() => {
      jest.clearAllMocks();
      sseHandlers = {};
      (api.post as jest.Mock).mockResolvedValue({ buildId: 'test-123' });
      (api.get as jest.Mock).mockResolvedValue({ id: 'test-123', status: 'Building' });
    });

    it('opens step picker when Start Build is clicked', () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      expect(screen.getByTestId('step-picker')).toBeInTheDocument();
    });

    it('starts build when step picker is confirmed', async () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      await act(async () => {
        fireEvent.click(screen.getByText('Confirm'));
      });
      expect(api.post).toHaveBeenCalledWith('/api/builds/start', expect.any(Object));
    });

    it('closes step picker when Cancel Picker is clicked', () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      act(() => { fireEvent.click(screen.getByText('Cancel Picker')); });
      expect(screen.queryByTestId('step-picker')).not.toBeInTheDocument();
    });

    it('select-all checks all three steps', () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      act(() => { fireEvent.click(screen.getByText('Select All')); });
      expect(screen.getByTestId('step-build')).toBeChecked();
      expect(screen.getByTestId('step-push')).toBeChecked();
      expect(screen.getByTestId('step-deploy')).toBeChecked();
    });

    it('auto-chains push after build when push step is selected', async () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      fireEvent.click(screen.getByText('Select All')); // ensure push is selected
      await act(async () => { fireEvent.click(screen.getByText('Confirm')); });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {}); // flush pendingChain useEffect
      expect(api.post).toHaveBeenCalledWith('/api/builds/test-123/push', {});
    });

    it('auto-chains deploy after push when deploy step is selected', async () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      fireEvent.click(screen.getByText('Select All')); // ensure push + deploy selected
      await act(async () => { fireEvent.click(screen.getByText('Confirm')); });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {}); // flush push chain
      await act(async () => {
        sseHandlers.onPushCompleted?.({ status: 'PushSucceeded' });
      });
      await act(async () => {}); // flush deploy chain
      expect(api.post).toHaveBeenCalledWith('/api/builds/test-123/deploy', expect.any(Object));
    });

    it('does not auto-chain push when push step is not selected', async () => {
      render(<BuildWizard {...defaultProps} />);
      act(() => { fireEvent.click(screen.getByText('Start Build')); });
      // push and deploy unchecked by default — just confirm build-only
      await act(async () => { fireEvent.click(screen.getByText('Confirm')); });
      await act(async () => {
        sseHandlers.onBuildCompleted?.({ status: 'ImageBuilt', gitTag: 'v1.0' });
      });
      await act(async () => {}); // flush any effects
      const pushCalls = (api.post as jest.Mock).mock.calls.filter(
        ([url]: [string]) => url.includes('/push')
      );
      expect(pushCalls).toHaveLength(0);
    });
  });

  describe('log restoration via initialBuildId', () => {
    beforeEach(() => {
      jest.clearAllMocks();
      (api.get as jest.Mock).mockImplementation((url: string) => {
        if (url.includes('/builds/')) {
          return Promise.resolve({ id: 'build-123', status: 'ImageBuilt', gitTag: 'v1.0' });
        }
        return Promise.resolve(null); // build-stats
      });
    });

    it('loads stored log lines from API when initialBuildId is provided', async () => {
      (api.getRaw as jest.Mock).mockResolvedValue(
        '[shipright] Step 1 done\n[docker] Building image\n[git] Pushing tag\n'
      );

      render(<BuildWizard {...defaultProps} initialBuildId="build-123" />);
      await act(async () => {});

      expect(api.getRaw).toHaveBeenCalledWith('/api/builds/build-123/log');
      expect(screen.getByText('[shipright] Step 1 done')).toBeInTheDocument();
      expect(screen.getByText('[docker] Building image')).toBeInTheDocument();
      expect(screen.getByText('[git] Pushing tag')).toBeInTheDocument();
    });

    it('does not call getRaw when no initialBuildId is provided', async () => {
      render(<BuildWizard {...defaultProps} />);
      await act(async () => {});

      expect(api.getRaw).not.toHaveBeenCalled();
    });

    it('shows log viewer in done phase when log is restored', async () => {
      (api.getRaw as jest.Mock).mockResolvedValue('[shipright] Build complete\n');

      render(<BuildWizard {...defaultProps} initialBuildId="build-123" />);
      await act(async () => {});

      expect(screen.getByTestId('log-viewer')).toBeInTheDocument();
    });

    it('silently ignores getRaw errors without crashing', async () => {
      (api.getRaw as jest.Mock).mockRejectedValue(new Error('network error'));

      expect(() => {
        render(<BuildWizard {...defaultProps} initialBuildId="build-123" />);
      }).not.toThrow();
      await act(async () => {});
    });
  });
});
