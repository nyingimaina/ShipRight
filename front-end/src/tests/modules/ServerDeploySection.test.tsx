import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import React from 'react';
import ServerDeploySection from '../../modules/ServerDeploy/ServerDeploySection';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn() },
  sseUrl: (p: string) => p,
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

const mocked = api as jest.Mocked<typeof api>;

let es: any;
beforeEach(() => {
  jest.clearAllMocks();
  es = undefined;
  (global as any).EventSource = class {
    onmessage: ((e: { data: string }) => void) | null = null;
    onerror: (() => void) | null = null;
    close = jest.fn();
    constructor(public url: string) { es = this; }
  };
});

const emit = (payload: object) =>
  act(() => { es.onmessage({ data: JSON.stringify(payload) }); });

const values = {
  composeRepoUrl: 'https://github.com/x/y.git',
  ecrRegistry: '123.dkr.ecr.us-east-2.amazonaws.com',
  ecrRegion: 'us-east-2',
  healthCheckUrl: '',
  envFile: 'A=1',
};

describe('ServerDeploySection', () => {
  it('renders the remote deploy fields', () => {
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);
    expect(screen.getByPlaceholderText(/github\.com/i)).toBeTruthy();
    expect(screen.getByPlaceholderText(/dkr\.ecr/i)).toBeTruthy();
    expect(screen.getByPlaceholderText('us-east-2')).toBeTruthy();
    expect(screen.getByPlaceholderText(/api\/health/)).toBeTruthy();
    expect(screen.getByPlaceholderText(/KEY=value/)).toBeTruthy();
  });

  it('gives every non-obvious field a help tip that says where to get the value', () => {
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);
    const expectations: [string, RegExp][] = [
      ['Compose repo URL', /Code.*HTTPS|HTTPS/i],
      ['ECR registry', /ECR.*Repositories|Repositories/i],
      ['ECR region', /top.?right|registry address/i],
      ['Health check URL (optional)', /leave blank.*localhost:5200/i],
      ['.env contents', /\.env\.example/i],
    ];
    for (const [label, pattern] of expectations) {
      fireEvent.mouseEnter(screen.getByRole('button', { name: `Help: ${label}` }));
      expect(screen.getByRole('tooltip').textContent).toMatch(pattern);
      fireEvent.mouseLeave(screen.getByRole('button', { name: `Help: ${label}` }));
    }
  });

  it('checks the app after deploy by default and shows the URL field', () => {
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);
    expect((screen.getByLabelText(/^Check that the app is running after each deploy$/i) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByPlaceholderText(/api\/health/)).toBeTruthy();
  });

  it('lets the user turn the health check off', () => {
    const onChange = jest.fn();
    render(<ServerDeploySection serverId="s1" values={values} onChange={onChange} />);
    fireEvent.click(screen.getByLabelText(/^Check that the app is running after each deploy$/i));
    expect(onChange).toHaveBeenCalledWith('skipHealthCheck', true);
  });

  it('hides the URL field and unticks the box when the check is skipped', () => {
    render(<ServerDeploySection serverId="s1" values={{ ...values, skipHealthCheck: true }} onChange={jest.fn()} />);
    expect((screen.getByLabelText(/^Check that the app is running after each deploy$/i) as HTMLInputElement).checked).toBe(false);
    expect(screen.queryByPlaceholderText(/api\/health/)).toBeNull();
  });

  it('explains the health check in a tip', () => {
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Help: Check that the app is running' }));
    expect(screen.getByRole('tooltip').textContent).toMatch(/turn (this )?off/i);
  });

  it('reports edits through onChange with the field name', () => {
    const onChange = jest.fn();
    render(<ServerDeploySection serverId="s1" values={values} onChange={onChange} />);
    fireEvent.change(screen.getByPlaceholderText('us-east-2'), { target: { value: 'eu-west-1' } });
    expect(onChange).toHaveBeenCalledWith('ecrRegion', 'eu-west-1');
  });

  it('disables actions until the server is saved', () => {
    render(<ServerDeploySection values={values} onChange={jest.fn()} />);
    expect((screen.getByText('Prepare server') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByText('Deploy now') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/save the server first/i)).toBeTruthy();
  });

  it('starts bootstrap and streams output lines', async () => {
    mocked.post.mockResolvedValue({ opId: 'op1' } as any);
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);

    fireEvent.click(screen.getByText('Prepare server'));

    await waitFor(() => expect(mocked.post).toHaveBeenCalledWith('/api/servers/s1/bootstrap', {}));
    await waitFor(() => expect(es).toBeDefined());
    expect(es.url).toBe('/api/servers/s1/ssh/ops/op1/stream');

    emit({ type: 'log', data: { source: 'stdout', line: '[bootstrap 1/6] Install Docker + git…' } });
    expect(await screen.findByText('[bootstrap 1/6] Install Docker + git…')).toBeTruthy();

    emit({ type: 'done', data: { exitCode: 0, message: 'Server is ready.' } });
    expect(await screen.findByText(/Server is ready\./)).toBeTruthy();
    expect(es.close).toHaveBeenCalled();
  });

  it('starts deploy against the deploy endpoint and shows failure', async () => {
    mocked.post.mockResolvedValue({ opId: 'op2' } as any);
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);

    fireEvent.click(screen.getByText('Deploy now'));

    await waitFor(() => expect(mocked.post).toHaveBeenCalledWith('/api/servers/s1/deploy', {}));
    await waitFor(() => expect(es).toBeDefined());

    emit({ type: 'done', data: { exitCode: 1, message: 'Health check failed after 12 attempts.' } });
    expect(await screen.findByText(/Health check failed after 12 attempts\./)).toBeTruthy();
  });

  it('shows an error when the request is rejected', async () => {
    mocked.post.mockRejectedValue({ message: 'No SSH key configured' });
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);

    fireEvent.click(screen.getByText('Prepare server'));

    expect(await screen.findByText(/No SSH key configured/)).toBeTruthy();
  });

  it('disables both buttons while an operation is running', async () => {
    mocked.post.mockResolvedValue({ opId: 'op3' } as any);
    render(<ServerDeploySection serverId="s1" values={values} onChange={jest.fn()} />);

    fireEvent.click(screen.getByText('Prepare server'));
    await waitFor(() => expect(es).toBeDefined());

    expect((screen.getByText('Prepare server') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByText('Deploy now') as HTMLButtonElement).disabled).toBe(true);
  });
});
