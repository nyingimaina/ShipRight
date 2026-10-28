import { render, screen, fireEvent, act, waitFor } from '@testing-library/react';
import ProjectDeployTab from '../ProjectDeployTab';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));
const mocked = api as jest.Mocked<typeof api>;

let es: any;
beforeEach(() => {
  jest.clearAllMocks();
  es = undefined;
  (global as any).EventSource = class {
    onmessage: any = null; onerror: any = null; close = jest.fn();
    constructor(public url: string) { es = this; }
  };
});
const emit = (p: object) => act(() => { es.onmessage({ data: JSON.stringify(p) }); });

describe('ProjectDeployTab', () => {
  it('deploys the project\'s server and streams the output into the log', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<ProjectDeployTab serverId="srv-1" projectName="My App" />);

    fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
    await waitFor(() => expect(es).toBeDefined());

    expect(mocked.post).toHaveBeenCalledWith('/api/servers/srv-1/deploy', {});
    expect(es.url).toBe('/api/servers/srv-1/ssh/ops/o1/stream');

    emit({ type: 'log', data: { line: 'Pulling api' } });
    expect(screen.getByText('Pulling api')).toBeInTheDocument();
  });

  it('shows the button as busy while running and reports success', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<ProjectDeployTab serverId="srv-1" projectName="My App" />);
    fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
    await waitFor(() => expect(es).toBeDefined());

    expect(screen.getByRole('button', { name: 'Deploying…' })).toBeDisabled();
    expect(screen.getByText('Deploy: running…')).toBeInTheDocument();

    emit({ type: 'done', data: { exitCode: 0, message: 'Deployed.' } });
    expect(screen.getByText('Deploy: succeeded')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deploy now' })).toBeEnabled();
  });

  it('reports a failed deploy', async () => {
    mocked.post.mockResolvedValue({ opId: 'o1' } as any);
    render(<ProjectDeployTab serverId="srv-1" projectName="My App" />);
    fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
    await waitFor(() => expect(es).toBeDefined());
    emit({ type: 'done', data: { exitCode: 1, message: 'compose up failed' } });
    expect(screen.getByText('Deploy: failed')).toBeInTheDocument();
  });

  it('explains itself and offers no action when the project has no server', () => {
    render(<ProjectDeployTab serverId={undefined} projectName="My App" />);
    expect(screen.getByText(/no server/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deploy now' })).toBeNull();
  });
});
