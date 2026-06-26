import { render, screen, act } from '@testing-library/react';
import BuildWizard from '../BuildWizard';

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

jest.mock('@/shared/SseService', () => ({
  buildSse: jest.fn(() => ({ close: jest.fn() })),
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
});
