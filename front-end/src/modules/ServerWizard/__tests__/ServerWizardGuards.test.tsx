import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import ServerWizard from '../ServerWizard';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;
const PUB = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIexample shipright';
const HOSTKEY = { sha256: 'SHA256:abc123', algorithm: 'ssh-ed25519', state: 'new', verifyCommand: 'ssh-keygen -lf x', message: 'First time.' };
const GOOD_TEST = { ready: true, summary: 'The server is ready.', items: [{ id: 'reach', title: 'Server is reachable', status: 'pass', message: 'ok' }] };

let statusHangs = false;
let statusCalls = 0;
let testHangs = false;

beforeEach(() => {
  jest.clearAllMocks();
  statusHangs = false; statusCalls = 0; testHangs = false;
  (navigator as unknown as { clipboard: unknown }).clipboard = { writeText: jest.fn().mockResolvedValue(undefined) };
  mockApi.get.mockImplementation(((path: string) => {
    if (path.endsWith('/host-key')) return Promise.resolve(HOSTKEY);
    if (path.endsWith('/ssh-key/status')) {
      statusCalls++;
      return statusHangs && statusCalls === 1 ? new Promise(() => {}) : Promise.resolve({ exists: true, publicKey: PUB, authorized: false });
    }
    return Promise.reject(new Error('unexpected GET ' + path));
  }) as never);
  mockApi.post.mockImplementation(((path: string) => {
    if (path === '/api/servers') return Promise.resolve({ id: 's1' });
    if (path.endsWith('/connection-test')) return testHangs ? new Promise(() => {}) : Promise.resolve(GOOD_TEST);
    return Promise.resolve({});
  }) as never);
  mockApi.put.mockResolvedValue({} as never);
});

afterEach(() => { jest.useRealTimers(); });

const next = () => fireEvent.click(screen.getByRole('button', { name: /^Next/ }));

async function toAccess() {
  render(<ServerWizard onClose={jest.fn()} onFinished={jest.fn()} />);
  fireEvent.click(screen.getByRole('radio', { name: /none of these/i })); next();
  await screen.findByLabelText(/^Name/);
  fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Box' } });
  fireEvent.change(screen.getByLabelText(/^Address/), { target: { value: 'box.example' } });
  next();
  await screen.findByText('SHA256:abc123');
  fireEvent.click(screen.getByRole('button', { name: /yes, trust this server/i }));
  await waitFor(() => expect(screen.getByText(/step 4 of 5/i)).toBeInTheDocument());
}

describe('step 4: the "I have added the key" switch', () => {
  it('Next stays disabled until the switch is turned on, and says why', async () => {
    await toAccess();
    const sw = await screen.findByRole('switch', { name: /i have added the key to the server/i });
    expect(sw).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByRole('button', { name: /^Next/ })).toBeDisabled();
    expect(screen.getByText(/turn on the switch once you have run the command/i)).toBeInTheDocument();
    fireEvent.click(sw);
    expect(sw).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByRole('button', { name: /^Next/ })).toBeEnabled();
    expect(screen.queryByText(/turn on the switch once you have run the command/i)).not.toBeInTheDocument();
  });

  it('turning the switch off again disables Next again', async () => {
    await toAccess();
    const sw = await screen.findByRole('switch', { name: /i have added the key/i });
    fireEvent.click(sw); fireEvent.click(sw);
    expect(screen.getByRole('button', { name: /^Next/ })).toBeDisabled();
  });

  it('there is no separate "I have run it" button any more', async () => {
    await toAccess();
    await screen.findByRole('switch', { name: /i have added the key/i });
    expect(screen.queryByRole('button', { name: /i have run it/i })).not.toBeInTheDocument();
  });
});

describe('step 4: preparing the key can no longer spin forever', () => {
  it('shows an error and a Try again after the wait, and Try again recovers', async () => {
    statusHangs = true;
    jest.useFakeTimers();
    render(<ServerWizard onClose={jest.fn()} onFinished={jest.fn()} />);
    fireEvent.click(screen.getByRole('radio', { name: /none of these/i })); next();
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Box' } });
    fireEvent.change(screen.getByLabelText(/^Address/), { target: { value: 'box.example' } });
    next();
    await act(async () => { await Promise.resolve(); });
    await screen.findByText('SHA256:abc123');
    fireEvent.click(screen.getByRole('button', { name: /yes, trust this server/i }));
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText(/preparing the key/i)).toBeInTheDocument();

    await act(async () => { jest.advanceTimersByTime(21000); });
    expect(await screen.findByText(/did not answer in time/i)).toBeInTheDocument();

    jest.useRealTimers();
    fireEvent.click(screen.getByRole('button', { name: /try again/i }));
    expect(await screen.findByRole('switch', { name: /i have added the key/i })).toBeInTheDocument();
  });
});

describe('step 5: the connection test can be cancelled', () => {
  it('offers Cancel while testing; cancelling frees the wizard and Test again re-runs', async () => {
    testHangs = true;
    await toAccess();
    fireEvent.click(await screen.findByRole('switch', { name: /i have added the key/i }));
    next();
    await screen.findByText(/step 5 of 5/i);
    const cancel = await screen.findByRole('button', { name: /cancel the test/i });
    fireEvent.click(cancel);
    expect(await screen.findByText(/test was cancelled/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /test again/i })).toBeEnabled();
    expect(screen.getByRole('button', { name: /^Finish/ })).toBeDisabled();
    testHangs = false;
    fireEvent.click(screen.getByRole('button', { name: /test again/i }));
    expect(await screen.findByText('The server is ready.')).toBeInTheDocument();
  });
});
