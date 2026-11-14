import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import JitComposeServices, { ComposeReading, ConfiguredService, ServiceHelp } from '../JitComposeServices';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));
jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: ({ onSelect, label }: { onSelect: (p: string) => void; label?: string }) =>
    <button type="button" onClick={() => onSelect(`D:\\work\\herd\\${label}`)}>{`choose ${label}`}</button>,
}));

const mockApi = api as jest.Mocked<typeof api>;

const reading = (over: Partial<ComposeReading> = {}): ComposeReading => ({
  relativePath: 'docker-compose.yml', repoRoot: 'D:\\work\\herd',
  services: [
    { name: 'backend', imageRef: 'reg/herd-backend', tagVariable: 'BACKEND_TAG', built: false, suggested: true, note: 'Its image line needs to read image: ${BACKEND_TAG:-reg/herd-backend:latest}.',
      dockerfile: 'backend.Dockerfile', context: '.', target: null, args: {}, argsNeedingAValue: [], otherStages: [] },
    { name: 'frontend', imageRef: 'reg/herd-frontend', tagVariable: 'FRONTEND_TAG', built: false, suggested: true, note: null,
      dockerfile: 'frontend.Dockerfile', context: '.', target: null, args: {}, argsNeedingAValue: ['NEXT_PUBLIC_API_URL'], otherStages: [] },
  ],
  leftAlone: ['db'], notes: [], ...over,
});

const help = (over: Partial<ServiceHelp> = {}): ServiceHelp => ({
  explanation: 'Building on the server needs to know which parts of your compose file to build and tag.',
  steps: ['Pick your compose file with Browse.', 'Or add the services to the project itself.'],
  found: null, ...over,
});

const backend = (over: Partial<ConfiguredService> = {}): ConfiguredService =>
  ({ name: 'backend', imageRef: 'reg/herd-backend', tagVariable: 'BACKEND_TAG', dockerfile: 'backend.Dockerfile', context: '.', target: null, args: {}, ...over });

let composeResult: unknown = reading();
let dockerfileResult: unknown = { repoRoot: 'D:\\work\\herd', dockerfileRelative: 'ops/api.Dockerfile', contextRelative: '.', dockerfileInContext: 'ops/api.Dockerfile', otherStages: [], argsNeedingAValue: [] };

beforeEach(() => {
  jest.clearAllMocks();
  composeResult = reading();
  dockerfileResult = { repoRoot: 'D:\\work\\herd', dockerfileRelative: 'ops/api.Dockerfile', contextRelative: '.', dockerfileInContext: 'ops/api.Dockerfile', otherStages: [], argsNeedingAValue: [] };
  mockApi.post.mockImplementation(async (path: string) => {
    if (path === '/api/jit/p1/compose-file') { if (composeResult instanceof Error) throw composeResult; return composeResult as never; }
    if (path === '/api/jit/p1/dockerfile') { if (dockerfileResult instanceof Error) throw dockerfileResult; return dockerfileResult as never; }
    throw new Error('unexpected POST ' + path);
  });
});

const setup = (props: Partial<React.ComponentProps<typeof JitComposeServices>> = {}) => {
  const onApply = jest.fn(); const onClear = jest.fn(); const onUpdate = jest.fn();
  render(<JitComposeServices base="/api/jit/p1" needsServices current={[]} help={null} onApply={onApply} onClear={onClear} onUpdate={onUpdate} {...props} />);
  return { onApply, onClear, onUpdate };
};

const browse = async () => {
  fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
  fireEvent.click(screen.getByRole('button', { name: /choose your compose file/i }));
  await screen.findByText('docker-compose.yml');
};

describe('picking the compose file', () => {
  it('reads the picked file and lists the services it can build, with their Dockerfiles and what to change', async () => {
    setup();
    await browse();
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/compose-file', { path: 'D:\\work\\herd\\Your compose file' });
    expect(screen.getByRole('checkbox', { name: 'Build backend' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Build frontend' })).toBeChecked();
    expect(screen.getByText('Dockerfile: backend.Dockerfile')).toBeInTheDocument();
    expect(screen.getByText(/left alone/i)).toBeInTheDocument();
  });

  it('applies the file path and the ticked services, with their Dockerfile and build folder', async () => {
    const { onApply } = setup();
    await browse();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Build frontend' }));
    fireEvent.click(screen.getByRole('button', { name: /use this compose file and these services/i }));
    expect(onApply).toHaveBeenCalledWith('docker-compose.yml', [
      { name: 'backend', imageRef: 'reg/herd-backend', tagVariable: 'BACKEND_TAG', dockerfile: 'backend.Dockerfile', context: '.', target: null, args: {} },
    ]);
  });

  it('keeps the arguments that still need a value, empty, so they can be filled in', async () => {
    const { onApply } = setup();
    await browse();
    fireEvent.click(screen.getByRole('button', { name: /use this compose file and these services/i }));
    const applied = onApply.mock.calls[0][1] as ConfiguredService[];
    expect(applied.find(s => s.name === 'frontend')!.args).toEqual({ NEXT_PUBLIC_API_URL: '' });
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
    fireEvent.click(screen.getByRole('checkbox', { name: 'Build frontend' }));
    expect(screen.getByRole('button', { name: /use this compose file and these services/i })).toBeDisabled();
  });

  it('says plainly when there is nothing to build in the file', async () => {
    composeResult = reading({ services: [], notes: ['There is nothing to build in this file: none of its services has a build section.'] });
    setup();
    fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: /choose your compose file/i }));
    expect(await screen.findByText(/nothing to build in this file/i)).toBeInTheDocument();
  });

  it('shows the server\'s reason when the file cannot be used', async () => {
    composeResult = Object.assign(new Error('x'), { isError: true, message: 'That file is not inside a git repository, so the server could not download it.' });
    setup();
    fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: /choose your compose file/i }));
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
  });

  it('offers the services ShipRight already found in the project folder', () => {
    const { onApply } = setup({ help: help({ found: reading() }) });
    expect(screen.getByText(/backend, frontend/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^use these services$/i }));
    const [file, services] = onApply.mock.calls[0] as [string, ConfiguredService[]];
    expect(file).toBe('docker-compose.yml');
    expect(services.map(s => s.dockerfile)).toEqual(['backend.Dockerfile', 'frontend.Dockerfile']);
  });

  it('offers no shortcut when nothing was found, and no box when there is nothing to explain', () => {
    setup({ help: help() });
    expect(screen.queryByRole('button', { name: /^use these services$/i })).not.toBeInTheDocument();
  });
});

describe('each service, its Dockerfile and its tag', () => {
  it('lists the services with the Dockerfile each is built from', () => {
    setup({ current: [backend(), { name: 'frontend', imageRef: 'reg/herd-frontend', tagVariable: 'FRONTEND_TAG', dockerfile: 'ops/fe.Dockerfile', context: 'web' }] });
    expect(screen.getByText(/services to build, from your compose file/i)).toBeInTheDocument();
    expect(screen.getByText('Dockerfile: backend.Dockerfile')).toBeInTheDocument();
    expect(screen.getByText('Dockerfile: ops/fe.Dockerfile')).toBeInTheDocument();
    expect(screen.getByText(/built from: web/i)).toBeInTheDocument();
  });

  it('asks for the Dockerfile of a service that has none', () => {
    setup({ current: [backend({ dockerfile: null })] });
    expect(screen.getByText(/pick this service's dockerfile/i)).toBeInTheDocument();
  });

  it('picks a Dockerfile with the file picker, and records where it is and what it builds from', async () => {
    const { onUpdate } = setup({ current: [backend({ dockerfile: null })] });
    fireEvent.click(screen.getByRole('button', { name: 'Browse for Dockerfile for backend' }));
    fireEvent.click(screen.getByRole('button', { name: /choose dockerfile for backend/i }));
    await waitFor(() => expect(onUpdate).toHaveBeenCalled());
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/dockerfile', { path: 'D:\\work\\herd\\Dockerfile for backend' });
    expect(onUpdate).toHaveBeenCalledWith('backend', { dockerfile: 'ops/api.Dockerfile', context: '.', args: {} });
  });

  it('adds empty slots for the arguments the Dockerfile needs, without touching values already given', async () => {
    dockerfileResult = { repoRoot: 'r', dockerfileRelative: 'ops/api.Dockerfile', contextRelative: '.', dockerfileInContext: 'ops/api.Dockerfile', otherStages: [], argsNeedingAValue: ['API_URL', 'MODE'] };
    const { onUpdate } = setup({ current: [backend({ args: { MODE: 'prod' } })] });
    fireEvent.click(screen.getByRole('button', { name: 'Browse for Dockerfile for backend' }));
    fireEvent.click(screen.getByRole('button', { name: /choose dockerfile for backend/i }));
    await waitFor(() => expect(onUpdate).toHaveBeenCalled());
    expect(onUpdate).toHaveBeenCalledWith('backend', { dockerfile: 'ops/api.Dockerfile', context: '.', args: { MODE: 'prod', API_URL: '' } });
  });

  it('shows the server\'s reason when the Dockerfile cannot be used', async () => {
    dockerfileResult = Object.assign(new Error('x'), { isError: true, message: 'That file is not inside a git repository, so the server could not download it.' });
    const { onUpdate } = setup({ current: [backend()] });
    fireEvent.click(screen.getByRole('button', { name: 'Browse for Dockerfile for backend' }));
    fireEvent.click(screen.getByRole('button', { name: /choose dockerfile for backend/i }));
    expect(await screen.findByText(/not inside a git repository/i)).toBeInTheDocument();
    expect(onUpdate).not.toHaveBeenCalled();
  });

  it('edits the image name, which is the part before the tag', () => {
    const { onUpdate } = setup({ current: [backend()] });
    fireEvent.change(screen.getByLabelText('Image name for backend'), { target: { value: 'reg/other-backend' } });
    expect(onUpdate).toHaveBeenCalledWith('backend', { imageRef: 'reg/other-backend' });
  });

  it('asks for the value of an argument that has none, and keeps the others', () => {
    const { onUpdate } = setup({ current: [backend({ args: { API_URL: '', MODE: 'prod' } })] });
    expect(screen.getByLabelText('Value for MODE in backend')).toHaveValue('prod');
    fireEvent.change(screen.getByLabelText('Value for API_URL in backend'), { target: { value: 'https://x' } });
    expect(onUpdate).toHaveBeenCalledWith('backend', { args: { API_URL: 'https://x', MODE: 'prod' } });
  });

  it('explains tagging in plain words: ShipRight adds it, nothing to type or change', () => {
    setup({ current: [backend()] });
    expect(screen.getByText(/gives every image a tag by itself/i)).toBeInTheDocument();
    expect(screen.getByText(/you do not need to change the image lines in your compose file/i)).toBeInTheDocument();
  });

  it('can clear the services', () => {
    const { onClear } = setup({ current: [backend()] });
    fireEvent.click(screen.getByRole('button', { name: /clear these services/i }));
    expect(onClear).toHaveBeenCalled();
  });

  it('shows nothing about services when there are none', () => {
    setup({ current: [] });
    expect(screen.queryByText(/services to build, from your compose file/i)).not.toBeInTheDocument();
  });
});
