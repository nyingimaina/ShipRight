import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import EnvDivergenceWarning from '../EnvDivergenceWarning';
import { api } from '@/shared/ApiService';
import type { EnvDivergenceResponse } from '../envDivergence';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn() },
}));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;
const BASE = '/api/jit/p1';

const divergent: EnvDivergenceResponse = {
  differs: true,
  serverFound: true,
  variables: [
    { name: 'SHIPRIGHT__DB_CONNECTION', serverRecordValue: 'Password=server-value', projectValue: 'Password=project-value' },
  ],
  serverRecordText: 'SHIPRIGHT__DB_CONNECTION=Password=server-value\n',
  projectText: 'SHIPRIGHT__DB_CONNECTION=Password=project-value\n',
};

const aligned: EnvDivergenceResponse = { ...divergent, differs: false, variables: [] };

function setup(response: EnvDivergenceResponse = divergent) {
  mockApi.get.mockImplementation(async (path: string) => {
    if (path.endsWith('/env-divergence')) return response as never;
    throw new Error('unexpected GET ' + path);
  });
  mockApi.post.mockResolvedValue({} as never);
  return render(<EnvDivergenceWarning base={BASE} />);
}

beforeEach(() => jest.clearAllMocks());

describe('EnvDivergenceWarning', () => {
  it('shows the name and both values so the difference is obvious', async () => {
    setup();
    const alert = await screen.findByRole('alert');

    expect(alert).toHaveTextContent('SHIPRIGHT__DB_CONNECTION');
    expect(alert).toHaveTextContent('Password=server-value');
    expect(alert).toHaveTextContent('Password=project-value');
  });

  it('stays quiet when the two copies already match', async () => {
    setup(aligned);
    await waitFor(() => expect(mockApi.get).toHaveBeenCalled());

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('using the server settings copies them into the project and clears the warning', async () => {
    setup();
    fireEvent.click(await screen.findByRole('button', { name: /use server settings/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith(`${BASE}/env-divergence/resolve`, { copy: 'to-project' }));
    await waitFor(() => expect(mockApi.get.mock.calls.filter(c => String(c[0]).endsWith('/env-divergence')).length).toBeGreaterThan(1));
  });

  it('using the project settings copies them onto the server record', async () => {
    setup();
    fireEvent.click(await screen.findByRole('button', { name: /use project settings/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith(`${BASE}/env-divergence/resolve`, { copy: 'to-server' }));
  });

  it('keeping both changes nothing and dismisses the warning', async () => {
    setup();
    fireEvent.click(await screen.findByRole('button', { name: /keep both/i }));

    expect(mockApi.post).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('does not offer to copy when there is no server record', async () => {
    setup({ ...divergent, serverFound: false });

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /use server settings/i })).not.toBeInTheDocument();
  });
});
