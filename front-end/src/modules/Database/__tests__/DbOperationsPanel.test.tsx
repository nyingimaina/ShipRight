import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import DbOperationsPanel from '../DbOperationsPanel';
import { api } from '@/shared/ApiService';

jest.mock('next/dynamic', () => ({
  __esModule: true,
  default: () => {
    const MockDynamic = (props: any) => <div data-testid="sql-query-panel" {...props} />;
    MockDynamic.displayName = 'MockDynamic';
    MockDynamic.preload = jest.fn();
    MockDynamic.Loadable = MockDynamic;
    return MockDynamic;
  },
}));

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn().mockResolvedValue([]),
    post: jest.fn(),
    delete: jest.fn(),
  },
  sseUrl: jest.fn((url: string) => url),
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled, zest }: any) => (
    <button
      onClick={onClick}
      disabled={disabled}
      data-variant={zest?.visualOptions?.variant}
    >
      {children}
    </button>
  ),
}));

jest.mock('jattac.libs.web.zest-tabs', () => {
  const MockZestTabs = ({ items, activeValue, onChange }: any) => (
    <div data-testid="zest-tabs">
      {items.map((item: any) => (
        <button
          key={item.value}
          data-testid={`tab-${item.value}`}
          onClick={() => onChange(item.value)}
          className={activeValue === item.value ? 'active' : ''}
        >
          {item.label}
        </button>
      ))}
    </div>
  );
  return {
    __esModule: true,
    default: MockZestTabs,
    ZestTabItem: {},
  };
});

jest.mock('../../BuildWizard/LogViewer', () => ({
  __esModule: true,
  default: ({ lines }: any) => (
    <div data-testid="log-viewer">
      {lines.map((l: any) => (
        <div key={l.id}>{l.line}</div>
      ))}
    </div>
  ),
}));

describe('DbOperationsPanel', () => {
  const defaultProps = {
    apiBase: '/api/projects/test-project/db',
    dbConfig: {
      provider: 'MariaDb' as const,
      containerName: 'test-container',
      databaseName: 'test-db',
      rootUser: 'root',
      rootPassword: '',
      backupRetainCount: 10,
    },
  };

  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('renders without crashing', () => {
    render(<DbOperationsPanel {...defaultProps} />);
    expect(screen.getByText('Backup Now')).toBeInTheDocument();
  });

  it('shows tabs when rendered', () => {
    render(<DbOperationsPanel {...defaultProps} />);
    expect(screen.getByTestId('zest-tabs')).toBeInTheDocument();
    expect(screen.getByTestId('tab-backup')).toBeInTheDocument();
    expect(screen.getByTestId('tab-sql')).toBeInTheDocument();
  });

  it('shows backup & restore section by default', () => {
    render(<DbOperationsPanel {...defaultProps} />);
    expect(screen.getByText('Backup Now')).toBeInTheDocument();
  });

  it('switches to SQL tab when clicked', async () => {
    render(<DbOperationsPanel {...defaultProps} />);
    fireEvent.click(screen.getByTestId('tab-sql'));
    await waitFor(() => {
      expect(screen.getByTestId('sql-query-panel')).toBeInTheDocument();
    });
  });

  it('switches back to backup tab when clicked', async () => {
    render(<DbOperationsPanel {...defaultProps} />);
    fireEvent.click(screen.getByTestId('tab-sql'));
    await waitFor(() => {
      expect(screen.getByTestId('sql-query-panel')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('tab-backup'));
    expect(screen.getByText('Backup Now')).toBeInTheDocument();
  });
});
