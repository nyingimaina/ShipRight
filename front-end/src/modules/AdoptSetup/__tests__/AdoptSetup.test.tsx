import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import AdoptSetup from '../AdoptSetup';
import { api } from '@/shared/ApiService';
import { AdoptedService, AdoptionPlan, ComposePreview } from '../adopt';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));
jest.mock('@/shared/timeZone', () => ({ getBrowserTimeZone: () => 'Africa/Nairobi' }));
jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: ({ onSelect }: { onSelect: (p: string) => void }) => <button type="button" onClick={() => onSelect('D:\\work\\herd')}>pick folder</button>,
}));

const mockApi = api as jest.Mocked<typeof api>;
const ROOT = 'D:\\work\\herd';

const svc = (over: Partial<AdoptedService> = {}): AdoptedService => ({
  name: 'backend', dockerfilePath: 'backend.Dockerfile', contextRelative: '.', target: null, buildArgs: {}, imageName: 'reg/herd-backend',
  composeService: 'backend', confidence: 'High', alternatives: ['back-end/dockerfile'], otherStages: [], argsNeedingAValue: [],
  tagStyle: 'SharedVariable', tagVariable: 'IMAGE_TAG', reasons: ['Its name matches backend.Dockerfile.'],
  versionFile: 'back-end/version.txt', suggestedVersionFile: 'back-end/version.txt', ...over,
});

const frontend = (over: Partial<AdoptedService> = {}) => svc({
  name: 'frontend', dockerfilePath: 'frontend.Dockerfile', composeService: 'frontend', imageName: 'reg/herd-frontend', alternatives: [],
  versionFile: null, suggestedVersionFile: 'front-end/version.txt', argsNeedingAValue: ['NEXT_PUBLIC_API_URL'], ...over,
});

const plan = (over: Partial<AdoptionPlan> = {}): AdoptionPlan => ({
  root: ROOT, composeFile: 'docker-compose.yml', services: [svc(), frontend()], thirdParty: ['db', 'minio'],
  unusedDockerfiles: ['deploy-starter-pack/backend.Dockerfile'], warnings: ['backend, frontend share one tag variable (IMAGE_TAG), so they cannot be versioned independently.'],
  repoRoot: ROOT, repoBranch: 'master', ...over,
});

const preview = (over: Partial<ComposePreview> = {}): ComposePreview => ({
  composeFile: 'docker-compose.yml',
  changes: [
    { service: 'backend', kind: 'image', before: 'image: reg/herd-backend:${IMAGE_TAG:-latest}', after: 'image: ${BACKEND_TAG:-reg/herd-backend:latest}' },
    { service: 'frontend', kind: 'image', before: 'image: reg/herd-frontend:${IMAGE_TAG:-latest}', after: 'image: ${FRONTEND_TAG:-reg/herd-frontend:latest}' },
  ],
  notes: ['backend (BACKEND_TAG), frontend (FRONTEND_TAG) now use their own tag variable.'], newText: '', ...over,
});

let currentPlan: AdoptionPlan = plan();
let currentPreview: ComposePreview = preview();
const posts: Array<[string, unknown]> = [];

beforeEach(() => {
  jest.clearAllMocks();
  posts.length = 0;
  currentPlan = plan(); currentPreview = preview();
  mockApi.get.mockImplementation(async (path: string) => {
    if (path === '/api/setup/wsl-home') return { home: '/home/u' } as never;
    throw new Error('unexpected GET ' + path);
  });
  mockApi.post.mockImplementation(async (path: string, body?: unknown) => {
    posts.push([path, body]);
    if (path === '/api/setup/adopt/plan') return { plan: currentPlan } as never;
    if (path === '/api/setup/adopt/compose-preview') return currentPreview as never;
    if (path === '/api/setup/adopt/version-files') return { created: ['front-end/version.txt'], alreadyThere: [], unknown: [] } as never;
    if (path === '/api/setup/adopt/compose-apply') return { written: true, backupFile: 'docker-compose.yml.shipright-backup', changes: [], notes: [] } as never;
    if (path === '/api/projects') return { id: 'p1', ...(body as object) } as never;
    throw new Error('unexpected POST ' + path);
  });
});

const setup = (props: Partial<React.ComponentProps<typeof AdoptSetup>> = {}) => {
  const onSaved = jest.fn(); const onCancel = jest.fn();
  render(<AdoptSetup onSaved={onSaved} onCancel={onCancel} {...props} />);
  return { onSaved, onCancel };
};

const review = async () => {
  fireEvent.click(screen.getByRole('button', { name: 'pick folder' }));
  fireEvent.click(screen.getByRole('button', { name: /look at this folder/i }));
  await screen.findByText(/found an existing docker setup/i);
};

const AFTER_BACKEND = 'image: ${BACKEND_TAG:-reg/herd-backend:latest}';
const callsTo = (path: string) => posts.filter(p => p[0] === path);

describe('looking at a folder', () => {
  it('shows what it found: services, what is left alone, what is unused and what to know', async () => {
    setup();
    await review();

    expect(callsTo('/api/setup/adopt/plan')[0][1]).toEqual({ rootPath: ROOT, choices: {} });
    expect(screen.getByRole('checkbox', { name: 'Adopt backend' })).toBeInTheDocument();
    expect(screen.getByText('Dockerfile: backend.Dockerfile')).toBeInTheDocument();
    expect(screen.getByText(/Image: reg\/herd-backend/)).toBeInTheDocument();
    expect(screen.getByText(/left alone/i)).toBeInTheDocument();
    expect(screen.getByText(/db, minio/)).toBeInTheDocument();
    expect(screen.getByText(/deploy-starter-pack\/backend\.Dockerfile/)).toBeInTheDocument();
    expect(screen.getByText(/share one tag variable/i)).toBeInTheDocument();
  });

  it('starts by itself when it is opened on a folder', async () => {
    setup({ initialRoot: ROOT });
    await screen.findByText(/found an existing docker setup/i);
    expect(callsTo('/api/setup/adopt/plan')).toHaveLength(1);
  });

  it('says so plainly when the folder cannot be read', async () => {
    mockApi.post.mockRejectedValueOnce({ isError: true, message: 'That folder does not exist.' });
    setup();
    fireEvent.click(screen.getByRole('button', { name: 'pick folder' }));
    fireEvent.click(screen.getByRole('button', { name: /look at this folder/i }));
    expect(await screen.findByText(/that folder does not exist/i)).toBeInTheDocument();
  });

  it('says so when there is nothing to adopt', async () => {
    currentPlan = plan({ services: [], composeFile: null, thirdParty: [], warnings: ['No Dockerfile was found in this folder or the folders below it.'] });
    setup();
    fireEvent.click(screen.getByRole('button', { name: 'pick folder' }));
    fireEvent.click(screen.getByRole('button', { name: /look at this folder/i }));
    expect(await screen.findByText(/no dockerfile was found/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /create project/i })).not.toBeInTheDocument();
  });
});

describe('settling what ShipRight could not decide', () => {
  it('asks which Dockerfile, then looks again with the answer', async () => {
    currentPlan = plan({ services: [svc({ dockerfilePath: null, confidence: 'Low', alternatives: ['a/worker/Dockerfile', 'b/worker/Dockerfile'], name: 'worker', composeService: 'worker', imageName: 'shop/worker' })], thirdParty: [] });
    setup();
    await review();
    expect(screen.getByRole('button', { name: /create project/i })).toBeDisabled();

    currentPlan = plan({ services: [svc({ name: 'worker', dockerfilePath: 'Dockerfile', contextRelative: 'a/worker', alternatives: ['b/worker/Dockerfile'], composeService: 'worker', imageName: 'shop/worker' })], thirdParty: [] });
    fireEvent.change(screen.getByLabelText('Dockerfile for worker'), { target: { value: 'a/worker/Dockerfile' } });
    await screen.findByText('Dockerfile: Dockerfile');
    expect(callsTo('/api/setup/adopt/plan').at(-1)![1]).toEqual({ rootPath: ROOT, choices: { worker: 'a/worker/Dockerfile' } });
    expect(screen.getByRole('button', { name: /create project/i })).toBeEnabled();
  });

  it('lets the person use a different Dockerfile than the one ShipRight picked', async () => {
    setup();
    await review();
    fireEvent.change(screen.getByLabelText('Dockerfile for backend'), { target: { value: 'back-end/dockerfile' } });
    await waitFor(() => expect(callsTo('/api/setup/adopt/plan')).toHaveLength(2));
    expect(callsTo('/api/setup/adopt/plan')[1][1]).toEqual({ rootPath: ROOT, choices: { backend: 'back-end/dockerfile' } });
  });

  it('asks for a build argument that has no value, and sends what is typed', async () => {
    setup();
    await review();
    fireEvent.change(screen.getByLabelText('Value for NEXT_PUBLIC_API_URL in frontend'), { target: { value: 'https://api.herd.example' } });
    fireEvent.click(screen.getByRole('button', { name: /create project/i }));
    await waitFor(() => expect(callsTo('/api/projects')).toHaveLength(1));
    const created = callsTo('/api/projects')[0][1] as { services: Array<{ name: string; buildArgs?: Record<string, string> }> };
    expect(created.services.find(s => s.name === 'frontend')!.buildArgs).toEqual({ NEXT_PUBLIC_API_URL: 'https://api.herd.example' });
  });
});

describe('the compose file', () => {
  it('shows exactly what would change, and offers to update it with a backup', async () => {
    setup();
    await review();
    await screen.findByText(AFTER_BACKEND);
    expect(callsTo('/api/setup/adopt/compose-preview')[0][1]).toEqual({ rootPath: ROOT, mode: 'registry', choices: {} });
    expect(screen.getByText(AFTER_BACKEND)).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /update my compose file \(a backup is kept\)/i })).toBeChecked();
  });

  it('previews again for the other way of deploying', async () => {
    setup();
    await review();
    await screen.findByText(AFTER_BACKEND);
    fireEvent.click(screen.getByRole('radio', { name: /build on the server/i }));
    await waitFor(() => expect(callsTo('/api/setup/adopt/compose-preview').at(-1)![1]).toEqual({ rootPath: ROOT, mode: 'build-on-server', choices: {} }));
  });

  it('has no compose section when there is no compose file', async () => {
    currentPlan = plan({ composeFile: null, thirdParty: [] });
    setup();
    await review();
    expect(callsTo('/api/setup/adopt/compose-preview')).toHaveLength(0);
    expect(screen.queryByRole('checkbox', { name: /update my compose file/i })).not.toBeInTheDocument();
  });

  it('has nothing to offer when no changes are needed', async () => {
    currentPlan = plan();
    currentPreview = preview({ changes: [], notes: [] });
    setup();
    await review();
    await screen.findByText(/already works with ShipRight/i);
    expect(screen.queryByRole('checkbox', { name: /update my compose file/i })).not.toBeInTheDocument();
  });
});

describe('creating the project', () => {
  it('creates missing version files, then updates the compose file, then saves the project, in that order', async () => {
    const { onSaved } = setup();
    await review();
    await screen.findByText(AFTER_BACKEND);
    fireEvent.change(screen.getByLabelText('Value for NEXT_PUBLIC_API_URL in frontend'), { target: { value: 'https://x' } });
    fireEvent.click(screen.getByRole('button', { name: /create project/i }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());

    const order = posts.map(p => p[0]).filter(p => /version-files|compose-apply|\/api\/projects/.test(p));
    expect(order).toEqual(['/api/setup/adopt/version-files', '/api/setup/adopt/compose-apply', '/api/projects']);
    expect(callsTo('/api/setup/adopt/version-files')[0][1]).toEqual({ rootPath: ROOT, services: ['frontend'] });
    expect(callsTo('/api/setup/adopt/compose-apply')[0][1]).toEqual({ rootPath: ROOT, mode: 'registry', choices: {} });

    const project = callsTo('/api/projects')[0][1] as { name: string; services: Array<Record<string, unknown>>; wsl: { workingDir: string } };
    expect(project.name).toBe('Herd');
    expect(project.services[0]).toMatchObject({ name: 'backend', dockerfilePath: 'backend.Dockerfile', buildContextPath: ROOT, dockerImageName: 'reg/herd-backend' });
    expect(project.wsl.workingDir).toBe('/home/u/.shipright/projects/herd');
    expect(toast.success).toHaveBeenCalled();
  });

  it('leaves the compose file alone when that box is unticked', async () => {
    const { onSaved } = setup();
    await review();
    fireEvent.click(await screen.findByRole('checkbox', { name: /update my compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: /create project/i }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(callsTo('/api/setup/adopt/compose-apply')).toHaveLength(0);
  });

  it('only creates version files for the services that need one', async () => {
    const { onSaved } = setup();
    await review();
    fireEvent.click(screen.getByRole('button', { name: /create project/i }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(callsTo('/api/setup/adopt/version-files')[0][1]).toEqual({ rootPath: ROOT, services: ['frontend'] });
  });

  it('leaves out a service that was unticked', async () => {
    const { onSaved } = setup();
    await review();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Adopt frontend' }));
    fireEvent.click(screen.getByRole('button', { name: /create project/i }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    const project = callsTo('/api/projects')[0][1] as { services: Array<{ name: string }> };
    expect(project.services.map(s => s.name)).toEqual(['backend']);
  });

  it('is disabled without a name or without any service ticked', async () => {
    setup();
    await review();
    fireEvent.change(screen.getByLabelText('Project name'), { target: { value: ' ' } });
    expect(screen.getByRole('button', { name: /create project/i })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Project name'), { target: { value: 'Herd' } });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Adopt backend' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Adopt frontend' }));
    expect(screen.getByRole('button', { name: /create project/i })).toBeDisabled();
  });

  it('reminds the person to save the changes in git before building', async () => {
    const { onSaved } = setup();
    await review();
    expect(screen.getByText(/commit these changes before your first build/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /create project/i }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
  });

  it('shows a plain message and saves nothing when a step fails', async () => {
    const { onSaved } = setup();
    await review();
    mockApi.get.mockRejectedValueOnce({ isError: true, message: 'WSL is not available.' });
    fireEvent.click(screen.getByRole('button', { name: /create project/i }));
    expect(await screen.findByText(/wsl is not available/i)).toBeInTheDocument();
    expect(onSaved).not.toHaveBeenCalled();
    expect(callsTo('/api/projects')).toHaveLength(0);
    expect(callsTo('/api/setup/adopt/compose-apply')).toHaveLength(0);
  });

  it('can be cancelled', async () => {
    const { onCancel } = setup();
    fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    expect(onCancel).toHaveBeenCalled();
  });
});
