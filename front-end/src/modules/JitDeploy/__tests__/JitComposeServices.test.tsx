import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import JitComposeServices, { ComposeReading, ServiceHelp } from '../JitComposeServices';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));
jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: ({ onSelect }: { onSelect: (p: string) => void }) => <button type="button" onClick={() => onSelect('D:\\work\\herd\\docker-compose.yml')}>choose file</button>,
}));

const mockApi = api as jest.Mocked<typeof api>;

const reading = (over: Partial<ComposeReading> = {}): ComposeReading => ({
  relativePath: 'docker-compose.yml', repoRoot: 'D:\\work\\herd',
  services: [
    { name: 'backend', imageRef: 'reg/herd-backend', tagVariable: 'BACKEND_TAG', built: false, suggested: true, note: 'Its image line needs to read image: ${BACKEND_TAG:-reg/herd-backend:latest}.' },
    { name: 'worker', imageRef: 'shop/worker', tagVariable: 'WORKER_TAG', built: true, suggested: true, note: null },
  ],
  leftAlone: ['db'], notes: [], ...over,
});

const help = (over: Partial<ServiceHelp> = {}): ServiceHelp => ({
  explanation: 'Building on the server needs to know which parts of your compose file to build and tag.',
  steps: ['Pick your compose file with Browse.', 'Or add the services to the project itself.'],
  found: null, ...over,
});

let result: unknown = reading();

beforeEach(() => {
  jest.clearAllMocks();
  result = reading();
  mockApi.post.mockImplementation(async (path: string) => {
    if (path === '/api/jit/p1/compose-file') { if (result instanceof Error) throw result; return result as never; }
    throw new Error('unexpected POST ' + path);
  });
});

const setup = (props: Partial<React.ComponentProps<typeof JitComposeServices>> = {}) => {
  const onApply = jest.fn(); const onClear = jest.fn();
  render(<JitComposeServices base="/api/jit/p1" needsServices current={[]} help={null} onApply={onApply} onClear={onClear} {...props} />);
  return { onApply, onClear };
};

const browse = async () => {
  fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
  fireEvent.click(screen.getByRole('button', { name: 'choose file' }));
  await screen.findByText('docker-compose.yml');
};

describe('picking the compose file', () => {
  it('reads the picked file and lists the services it can build, with what to change', async () => {
    setup();
    await browse();
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/compose-file', { path: 'D:\\work\\herd\\docker-compose.yml' });
    expect(screen.getByRole('checkbox', { name: 'Build backend' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Build worker' })).toBeChecked();
    expect(screen.getByText(/image line needs to read image: \$\{BACKEND_TAG/)).toBeInTheDocument();
    expect(screen.getByText(/left alone/i)).toBeInTheDocument();
    expect(screen.getByText(/db/)).toBeInTheDocument();
  });

  it('applies the file path and the ticked services', async () => {
    const { onApply } = setup();
    await browse();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Build worker' }));
    fireEvent.click(screen.getByRole('button', { name: /use this compose file and these services/i }));
    expect(onApply).toHaveBeenCalledWith('docker-compose.yml', [{ name: 'backend', imageRef: 'reg/herd-backend', tagVariable: 'BACKEND_TAG' }]);
  });

  it('only sets the file when the project already has its own services', async () => {
    const { onApply } = setup({ needsServices: false });
    await browse();
    expect(screen.queryByRole('checkbox', { name: 'Build backend' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /use this compose file$/i }));
    expect(onApply).toHaveBeenCalledWith('docker-compose.yml', null);
  });

  it('cannot apply when no service is ticked', async () => {
    setup();
    await browse();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Build backend' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Build worker' }));
    expect(screen.getByRole('button', { name: /use this compose file and these services/i })).toBeDisabled();
  });

  it('says plainly when there is nothing to build in the file', async () => {
    result = reading({ services: [], notes: ['There is nothing to build in this file: none of its services has a build section.'] });
    setup();
    fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: 'choose file' }));
    expect(await screen.findByText(/nothing to build in this file/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /use this compose file and these services/i })).not.toBeInTheDocument();
  });

  it('shows the server\'s reason when the file cannot be used', async () => {
    result = Object.assign(new Error('x'), { isError: true, message: 'That file is not inside a git repository, so the server could not download it.' });
    setup();
    fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: 'choose file' }));
    expect(await screen.findByText(/not inside a git repository/i)).toBeInTheDocument();
  });

  it('can be cancelled', () => {
    setup();
    fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    expect(screen.getByRole('button', { name: /browse for the compose file/i })).toBeInTheDocument();
  });
});

describe('when the project has no services', () => {
  it('explains why, and what to do, step by step', () => {
    setup({ help: help() });
    expect(screen.getByText(/needs to know which parts of your compose file/i)).toBeInTheDocument();
    expect(screen.getByText('Pick your compose file with Browse.')).toBeInTheDocument();
    expect(screen.getByText('Or add the services to the project itself.')).toBeInTheDocument();
  });

  it('offers the services ShipRight already found in the project folder', () => {
    const { onApply } = setup({ help: help({ found: reading() }) });
    expect(screen.getByText(/backend, worker/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^use these services$/i }));
    expect(onApply).toHaveBeenCalledWith('docker-compose.yml', [
      { name: 'backend', imageRef: 'reg/herd-backend', tagVariable: 'BACKEND_TAG' },
      { name: 'worker', imageRef: 'shop/worker', tagVariable: 'WORKER_TAG' },
    ]);
  });

  it('offers no shortcut when nothing was found', () => {
    setup({ help: help() });
    expect(screen.queryByRole('button', { name: /^use these services$/i })).not.toBeInTheDocument();
  });

  it('has no help box when there is nothing to explain', () => {
    setup({ help: null });
    expect(screen.queryByText(/needs to know which parts of your compose file/i)).not.toBeInTheDocument();
  });
});

describe('services already taken from the compose file', () => {
  it('lists them, and can clear them', () => {
    const { onClear } = setup({ current: [{ name: 'backend', imageRef: 'reg/herd-backend', tagVariable: 'BACKEND_TAG' }, { name: 'worker', imageRef: 'shop/worker', tagVariable: 'WORKER_TAG' }] });
    expect(screen.getByText(/services to build, from your compose file/i)).toBeInTheDocument();
    expect(screen.getByText(/backend, worker/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /clear these services/i }));
    expect(onClear).toHaveBeenCalled();
  });

  it('shows nothing about them when there are none', () => {
    setup({ current: [] });
    expect(screen.queryByText(/services to build, from your compose file/i)).not.toBeInTheDocument();
    waitFor(() => undefined);
  });
});
