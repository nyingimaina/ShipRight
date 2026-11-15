import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import toast from 'react-hot-toast';
import ServerWizard from '../ServerWizard';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;
const PUB = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIexample shipright';

const HOSTKEY = { sha256: 'SHA256:abc123', algorithm: 'ssh-ed25519', state: 'new', verifyCommand: 'ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub', message: 'First time.' };
const GOOD_TEST = {
  ready: true, summary: 'The server is ready.',
  items: [
    { id: 'reach', title: 'Server is reachable', status: 'pass', message: 'ShipRight can reach the server.' },
    { id: 'docker', title: 'Docker', status: 'warn', message: 'Docker is not installed yet. ShipRight installs it automatically on the first deploy.' },
  ],
};
const BAD_TEST = {
  ready: false, summary: 'Not ready: The server refused the key.',
  items: [
    { id: 'reach', title: 'Server is reachable', status: 'pass', message: 'ok' },
    { id: 'login', title: 'Login works', status: 'fail', message: 'The server refused the key for user ubuntu.', fix: 'Add the key to authorized_keys.' },
    { id: 'folder', title: 'Deploy folder', status: 'fail', message: 'Cannot write.', fix: 'Run this.', command: "sudo mkdir -p '/srv/x' && sudo chown \"$USER\" '/srv/x'" },
  ],
};

let keyExists = true;
let hostKey: unknown = HOSTKEY;
let testResponses: unknown[] = [GOOD_TEST];

beforeEach(() => {
  jest.clearAllMocks();
  keyExists = true; hostKey = HOSTKEY; testResponses = [GOOD_TEST];
  (navigator as unknown as { clipboard: unknown }).clipboard = { writeText: jest.fn().mockResolvedValue(undefined) };
  mockApi.get.mockImplementation(async (path: string) => {
    if (path.endsWith('/host-key')) { if (hostKey instanceof Error) throw { isError: true, message: (hostKey as Error).message }; return hostKey as never; }
    if (path.endsWith('/ssh-key/status')) return { exists: keyExists, publicKey: keyExists ? PUB : null, authorized: false } as never;
    if (path.startsWith('/api/resources/key-files')) return { items: [{ id: 'k1', name: 'aws.pem' }] } as never;
    if (path === '/api/servers/s1') return { id: 's1', name: 'Box', host: 'box.example', username: 'ubuntu', remoteWorkingDir: '/srv/x', sshKeyPath: '/k', deployMode: 'GitScript' } as never;
    throw new Error('unexpected GET ' + path);
  });
  mockApi.post.mockImplementation(async (path: string) => {
    if (path === '/api/servers') return { id: 's1' } as never;
    if (path.endsWith('/ssh-key/generate')) { keyExists = true; return {} as never; }
    if (path.endsWith('/connection-test')) return (testResponses.length > 1 ? testResponses.shift() : testResponses[0]) as never;
    return {} as never;
  });
  mockApi.put.mockResolvedValue({} as never);
});

const setup = (props: Partial<React.ComponentProps<typeof ServerWizard>> = {}) => {
  const onClose = jest.fn(); const onFinished = jest.fn();
  render(<ServerWizard onClose={onClose} onFinished={onFinished} {...props} />);
  return { onClose, onFinished };
};

const next = () => fireEvent.click(screen.getByRole('button', { name: 'Next' }));
const choose = (re: RegExp) => fireEvent.click(screen.getByRole('radio', { name: re }));

async function toWhere(route: RegExp = /username and password/i) {
  choose(route); next();
  await screen.findByLabelText(/^Name/);
}

async function fillWhere(host = 'box.example') {
  fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Box' } });
  fireEvent.change(screen.getByLabelText(/^Address/), { target: { value: host } });
}

async function toIdentity(route: RegExp = /username and password/i) {
  await toWhere(route); await fillWhere(); next();
  await screen.findByText('SHA256:abc123');
}

async function toAccess(route: RegExp = /username and password/i) {
  await toIdentity(route);
  fireEvent.click(screen.getByRole('button', { name: /yes, trust this server/i }));
  await waitFor(() => expect(screen.getByText(/step 4 of 5/i)).toBeInTheDocument());
}

describe('step 1: how do you log in', () => {
  it('shows the four routes, and Next waits for a choice', () => {
    setup();
    expect(screen.getByText(/step 1 of 5/i)).toBeInTheDocument();
    expect(screen.getAllByRole('radio')).toHaveLength(4);
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    choose(/tailscale/i);
    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();
  });

  it('closes from the close button', () => {
    const { onClose } = setup();
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalled();
  });
});

describe('step 2: where is it', () => {
  it('explains every mistake and saves nothing until the form is right', async () => {
    setup();
    await toWhere();
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText(/^Address/), { target: { value: 'ubuntu@box' } });
    next();
    expect(await screen.findByText(/give the server a name/i)).toBeInTheDocument();
    expect(screen.getByText(/the username goes in the next box/i)).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('creates the server without a key, suggesting a folder, then moves on', async () => {
    setup();
    await toWhere(); await fillWhere(); next();
    await screen.findByText(/step 3 of 5/i);
    expect(mockApi.post).toHaveBeenCalledWith('/api/servers', expect.objectContaining({
      name: 'Box', host: 'box.example', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '/home/ubuntu/apps',
    }));
  });

  it('updates the same server when coming back, instead of creating a second one', async () => {
    setup();
    await toIdentity();
    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    await screen.findByLabelText(/^Name/);
    next();
    await screen.findByText(/step 3 of 5/i);
    expect(mockApi.post.mock.calls.filter(c => c[0] === '/api/servers')).toHaveLength(1);
    expect(mockApi.put).toHaveBeenCalledWith('/api/servers/s1', expect.objectContaining({ host: 'box.example', sshKeyPath: '/k' }));
  });

  it('shows a plain message when saving fails', async () => {
    mockApi.post.mockRejectedValueOnce({ isError: true, message: 'Host is required.' });
    setup();
    await toWhere(); await fillWhere(); next();
    expect(await screen.findByText('Host is required.')).toBeInTheDocument();
    expect(screen.getByText(/step 2 of 5/i)).toBeInTheDocument();
  });

  it('gently notes an address that does not look like Tailscale on the Tailscale route', async () => {
    setup();
    await toWhere(/tailscale/i);
    fireEvent.change(screen.getByLabelText(/^Address/), { target: { value: 'example.com' } });
    expect(screen.getByText(/does not look like a tailscale address/i)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/^Address/), { target: { value: 'box.tail1.ts.net' } });
    expect(screen.queryByText(/does not look like a tailscale address/i)).not.toBeInTheDocument();
  });
});

describe('step 3: is this the right server', () => {
  it('shows the fingerprint and how to check it, and trusts only what was shown', async () => {
    setup();
    await toIdentity();
    expect(screen.getByText('ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /yes, trust this server/i }));
    await screen.findByText(/step 4 of 5/i);
    expect(mockApi.post).toHaveBeenCalledWith('/api/servers/s1/host-key/trust', { sha256: 'SHA256:abc123', acknowledgeChange: false });
  });

  it('copies the check command', async () => {
    setup();
    await toIdentity();
    fireEvent.click(screen.getByRole('button', { name: /copy the fingerprint check command/i }));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith('ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub'));
    expect(toast.success).toHaveBeenCalled();
  });

  it('a changed identity needs an explicit confirmation before it can be trusted', async () => {
    hostKey = { ...HOSTKEY, state: 'changed', message: 'This is different from before.' };
    setup();
    await toIdentity();
    expect(screen.getByText(/different from before/i)).toBeInTheDocument();
    const trustBtn = screen.getByRole('button', { name: /yes, trust this server/i });
    expect(trustBtn).toBeDisabled();
    fireEvent.click(screen.getByRole('checkbox'));
    expect(trustBtn).toBeEnabled();
    fireEvent.click(trustBtn);
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/servers/s1/host-key/trust', { sha256: 'SHA256:abc123', acknowledgeChange: true }));
  });

  it('an already trusted server just continues', async () => {
    hostKey = { ...HOSTKEY, state: 'trusted', message: 'Already trusted.' };
    setup();
    await toIdentity();
    expect(screen.queryByRole('button', { name: /yes, trust this server/i })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText(/step 4 of 5/i);
  });

  it('explains a server that cannot be read, and lets the user try again', async () => {
    hostKey = new Error('ShipRight could not read the server\'s identity. Check that SSH is running.');
    setup();
    await toWhere(); await fillWhere(); next();
    expect(await screen.findByText(/could not read the server's identity/i)).toBeInTheDocument();
    hostKey = HOSTKEY;
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('SHA256:abc123')).toBeInTheDocument();
  });
});

describe('step 4: give ShipRight access', () => {
  it('password route: adds the key with the password, never keeps it, and unlocks Next', async () => {
    setup();
    await toAccess();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    const box = await screen.findByLabelText(/server password/i);
    fireEvent.change(box, { target: { value: 'hunter2' } });
    fireEvent.click(screen.getByRole('button', { name: /add the key/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/servers/s1/ssh-key/authorize', { password: 'hunter2', port: 22 }));
    await screen.findByText(/can use its key on this server/i);
    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();
  });

  it('generates the key once, only when there is none', async () => {
    keyExists = false;
    setup();
    await toAccess();
    await screen.findByLabelText(/server password/i);
    expect(mockApi.post.mock.calls.filter(c => String(c[0]).endsWith('/ssh-key/generate'))).toHaveLength(1);
  });

  it('does not replace an existing key', async () => {
    setup();
    await toAccess();
    await screen.findByLabelText(/server password/i);
    expect(mockApi.post.mock.calls.some(c => String(c[0]).endsWith('/ssh-key/generate'))).toBe(false);
  });

  it('shows the server\'s own refusal when the password is wrong', async () => {
    mockApi.post.mockImplementation(async (path: string) => {
      if (path === '/api/servers') return { id: 's1' } as never;
      if (path.endsWith('/ssh-key/authorize')) throw { isError: true, message: 'Authorization failed: Permission denied' };
      return {} as never;
    });
    setup();
    await toAccess();
    fireEvent.change(await screen.findByLabelText(/server password/i), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: /add the key/i }));
    expect(await screen.findByText(/permission denied/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('password route: switches to doing it by hand when the server does not allow passwords', async () => {
    setup();
    await toAccess();
    fireEvent.click(await screen.findByRole('button', { name: /does not allow password login/i }));
    expect(await screen.findByText(new RegExp(`grep -qxF '${PUB}'`))).toBeInTheDocument();
  });

  it('manual route: shows the one-line command with the real key, and copies it', async () => {
    setup();
    await toAccess(/none of these/i);
    const cmd = await screen.findByText(new RegExp(`echo '${PUB}' >> ~/.ssh/authorized_keys`));
    expect(cmd.textContent).toContain('chmod 600 ~/.ssh/authorized_keys');
    fireEvent.click(screen.getByRole('button', { name: /copy the command that adds the key/i }));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(expect.stringContaining(`echo '${PUB}'`)));
    expect(screen.getByText(/run this on the server/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    fireEvent.click(screen.getByRole('switch', { name: /i have added the key/i }));
    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();
  });

  it('tailscale route uses the same by-hand step, with Tailscale wording', async () => {
    setup();
    await toAccess(/tailscale/i);
    expect(await screen.findByText(/over tailscale/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/server password/i)).not.toBeInTheDocument();
  });

  it('never tells the user to run anything on their own computer', async () => {
    setup();
    await toAccess(/tailscale/i);
    await screen.findByText(/over tailscale/i);
    expect(document.body.textContent).not.toMatch(/\bssh [a-z]+@/i);
  });

  it('key file route: lists saved key files and uses the chosen one', async () => {
    setup();
    await toAccess(/key file/i);
    const select = await screen.findByLabelText(/saved key file/i);
    await screen.findByRole('option', { name: 'aws.pem' });
    fireEvent.change(select, { target: { value: 'k1' } });
    fireEvent.click(screen.getByRole('button', { name: /add the key/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/servers/s1/ssh-key/authorize', { mode: 'keyfile', keyFileResourceId: 'k1', port: 22 }));
  });

  it('says so when ShipRight cannot make its key', async () => {
    mockApi.get.mockImplementation(async (path: string) => {
      if (path.endsWith('/host-key')) return HOSTKEY as never;
      if (path.endsWith('/ssh-key/status')) throw { isError: true, message: 'Key generation is not available on this machine.' };
      throw new Error('unexpected ' + path);
    });
    setup();
    await toAccess();
    expect(await screen.findByText(/key generation is not available/i)).toBeInTheDocument();
  });
});

describe('step 5: test the connection', () => {
  async function toTest() {
    setup();
    await toAccess(/none of these/i);
    fireEvent.click(await screen.findByRole('switch', { name: /i have added the key/i }));
    next();
    await screen.findByText(/step 5 of 5/i);
  }

  it('runs the test by itself and shows every check with a symbol and words', async () => {
    await toTest();
    expect(await screen.findByText('The server is ready.')).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledWith('/api/servers/s1/connection-test', {});
    expect(screen.getByRole('img', { name: 'Passed' })).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /needs attention/i })).toBeInTheDocument();
    expect(screen.getByText(/installs it automatically on the first deploy/i)).toBeInTheDocument();
  });

  it('finishes with the server id once the server is ready', async () => {
    const { onFinished } = (() => { const o = { onClose: jest.fn(), onFinished: jest.fn() }; return o; })();
    render(<ServerWizard onClose={jest.fn()} onFinished={onFinished} />);
    choose(/none of these/i); next();
    await screen.findByLabelText(/^Name/); await fillWhere(); next();
    await screen.findByText('SHA256:abc123');
    fireEvent.click(screen.getByRole('button', { name: /yes, trust this server/i }));
    fireEvent.click(await screen.findByRole('switch', { name: /i have added the key/i }));
    next();
    await screen.findByText('The server is ready.');
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    expect(onFinished).toHaveBeenCalledWith('s1');
  });

  it('a failure blocks Finish, shows the fix and its command, and offers to go back', async () => {
    testResponses = [BAD_TEST];
    await toTest();
    await screen.findByText(/not ready: the server refused the key/i);
    expect(screen.getByRole('button', { name: 'Finish' })).toBeDisabled();
    expect(screen.getByText('Add the key to authorized_keys.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /copy the command for deploy folder/i }));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(expect.stringContaining("sudo mkdir -p '/srv/x'")));
    fireEvent.click(screen.getByRole('button', { name: /go back to adding the key/i }));
    await screen.findByText(/step 4 of 5/i);
  });

  it('Test again re-runs and can turn a failure into a pass', async () => {
    testResponses = [BAD_TEST, GOOD_TEST];
    await toTest();
    await screen.findByText(/not ready/i);
    fireEvent.click(screen.getByRole('button', { name: 'Test again' }));
    await screen.findByText('The server is ready.');
    expect(screen.getByRole('button', { name: 'Finish' })).toBeEnabled();
  });

  it('shows a readable failure when the test itself cannot run', async () => {
    mockApi.post.mockImplementation(async (path: string) => {
      if (path === '/api/servers') return { id: 's1' } as never;
      if (path.endsWith('/connection-test')) throw { isError: true, message: 'Server not found.' };
      return {} as never;
    });
    setup();
    await toAccess(/none of these/i);
    fireEvent.click(await screen.findByRole('switch', { name: /i have added the key/i }));
    next();
    expect(await screen.findByText(/server not found/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Finish' })).toBeDisabled();
  });
});

describe('opening on an existing server', () => {
  it('starts at the identity check with the saved details, to fix a connection', async () => {
    setup({ existingServerId: 's1' });
    expect(await screen.findByText('SHA256:abc123')).toBeInTheDocument();
    expect(screen.getByText(/step 3 of 5/i)).toBeInTheDocument();
    expect(mockApi.get).toHaveBeenCalledWith('/api/servers/s1/host-key');
    expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
  });
});
