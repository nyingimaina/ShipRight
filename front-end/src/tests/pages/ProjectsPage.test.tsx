import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import React from 'react';
import ProjectList from '../../pages/projects';
import { api } from '@/shared/ApiService';

const push = jest.fn();
const replace = jest.fn();
let query: Record<string, string> = {};

jest.mock('next/router', () => ({ useRouter: () => ({ query, push: (...a: unknown[]) => push(...a), replace: (...a: unknown[]) => replace(...a) }) }));
jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn() }, sseUrl: (p: string) => p }));
jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: { children: React.ReactNode }) => <>{children}</> }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, zest, ...rest }: { children?: React.ReactNode; onClick?: () => void; zest?: { semanticType?: string } }) => (
    <button type="button" onClick={onClick} data-semantic={zest?.semanticType} {...rest}>{children}</button>
  ),
}));
jest.mock('jattac.libs.web.overflow-menu', () => ({
  __esModule: true,
  default: ({ items }: { items: { content: string; onClick: () => void }[] }) => (
    <div>{items.map(i => <button type="button" key={i.content} onClick={i.onClick}>{i.content}</button>)}</div>
  ),
}));
jest.mock('jattac.libs.web.zest-responsive-layout', () => ({
  ZestResponsiveLayout: ({ children, sidePane }: { children: React.ReactNode; sidePane?: { visible: boolean; title: string } }) => (
    <div>{children}{sidePane?.visible && <aside aria-label="side pane">{sidePane.title}</aside>}</div>
  ),
}));
jest.mock('../../modules/AppShell/AppShell', () => ({ __esModule: true, default: ({ children }: { children: React.ReactNode }) => <div>{children}</div> }));
jest.mock('../../modules/ProjectConfig/QuickSetup/ProjectPane', () => ({ __esModule: true, default: () => <div>pane</div> }));
jest.mock('../../modules/AdoptSetup/AdoptLauncher', () => ({ __esModule: true, default: () => <span>adopt</span> }));
jest.mock('../../modules/ProjectDetail/ProjectDetailPanel', () => ({ __esModule: true, default: ({ projectId }: { projectId: string }) => <div>detail of {projectId}</div> }));
jest.mock('../../modules/ProjectTransfer/ImportBundleDialog', () => ({ __esModule: true, default: () => <div role="dialog">import dialog</div> }));
jest.mock('../../modules/ProjectTransfer/exportProject', () => ({ exportProject: jest.fn().mockResolvedValue(undefined) }));

const mocked = api as jest.Mocked<typeof api>;

const SHOP = {
  id: 'p1', name: 'Shop', type: 'Pipeline',
  services: [{ name: 'api' }, { name: 'web' }],
  server: { name: 'Prod', host: '1.2.3.4' },
};
const BLOG = { id: 'p2', name: 'Blog', type: 'Freeform', services: [{ name: 'site' }], server: { host: '5.6.7.8' } };

beforeEach(() => {
  jest.clearAllMocks();
  query = {};
  mocked.get.mockImplementation((async (url: string) => (url === '/api/projects' ? [SHOP, BLOG] : [])) as never);
  mocked.delete.mockResolvedValue(undefined as never);
});

describe('project cards', () => {
  it('shows every project with its services and a one-line summary', async () => {
    render(<ProjectList />);
    expect(await screen.findByText('Shop')).toBeInTheDocument();
    expect(screen.getByText('Blog')).toBeInTheDocument();
    expect(screen.getByText('api')).toBeInTheDocument();
    expect(screen.getByText(/Pipeline · 2 services · Prod/)).toBeInTheDocument();
    expect(screen.getByText(/Freeform · 1 service · 5\.6\.7\.8/)).toBeInTheDocument();
  });

  it('opens the project when the card is clicked anywhere, with no "View detail" button needed', async () => {
    render(<ProjectList />);
    fireEvent.click(await screen.findByText('api'));      // a service chip, not the title
    expect(push).toHaveBeenCalledWith('/projects/?detail=p1', undefined, { shallow: true });
    expect(screen.queryByRole('button', { name: /view detail/i })).not.toBeInTheDocument();
  });

  it('the project name is a real link, so the keyboard and "open in new tab" work, and it opens once', async () => {
    render(<ProjectList />);
    const link = await screen.findByRole('link', { name: 'Shop' });
    expect(link).toHaveAttribute('href', '/projects/?detail=p1');
    fireEvent.click(link);
    expect(push).toHaveBeenCalledTimes(1);
  });

  it('the menu does not open the project; its actions do their own thing', async () => {
    render(<ProjectList />);
    const card = (await screen.findByText('Shop')).closest('article') as HTMLElement;
    fireEvent.click(within(card).getByText('Edit'));
    expect(await screen.findByLabelText('side pane')).toHaveTextContent('Edit: Shop');
    expect(push).not.toHaveBeenCalled();

    fireEvent.click(within(card).getByText('Delete'));
    await waitFor(() => expect(mocked.delete).toHaveBeenCalledWith('/api/projects/p1'));
    await waitFor(() => expect(screen.queryByText('Shop')).not.toBeInTheDocument());
    expect(push).not.toHaveBeenCalled();
  });

  it('uses no inline styles anywhere on the page', async () => {
    const { container } = render(<ProjectList />);
    await screen.findByText('Shop');
    expect(container.querySelectorAll('[style]')).toHaveLength(0);
  });
});

describe('page actions', () => {
  it('uses Zest buttons with meaningful types for New project and Import', async () => {
    render(<ProjectList />);
    await screen.findByText('Shop');
    expect(screen.getByRole('button', { name: 'New Project' })).toHaveAttribute('data-semantic', 'new');
    expect(screen.getByRole('button', { name: 'Import' })).toHaveAttribute('data-semantic', 'import');
  });

  it('New project opens the pane and Import opens its dialog', async () => {
    render(<ProjectList />);
    await screen.findByText('Shop');
    fireEvent.click(screen.getByRole('button', { name: 'New Project' }));
    expect(await screen.findByLabelText('side pane')).toHaveTextContent('New Project');
    fireEvent.click(screen.getByRole('button', { name: 'Import' }));
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('says how many projects there are', async () => {
    render(<ProjectList />);
    expect(await screen.findByText('2 projects')).toBeInTheDocument();
  });
});

describe('when there are no projects', () => {
  beforeEach(() => { mocked.get.mockImplementation((async () => []) as never); });

  it('explains what a project is and offers the next step as Zest buttons', async () => {
    render(<ProjectList />);
    expect(await screen.findByText('No projects yet')).toBeInTheDocument();
    const create = screen.getAllByRole('button', { name: /new project/i }).find(b => b.closest('section'))!;
    expect(create).toHaveAttribute('data-semantic', 'new');
    fireEvent.click(create);
    expect(await screen.findByLabelText('side pane')).toBeInTheDocument();
  });

  it('has no inline styles in the empty state either', async () => {
    const { container } = render(<ProjectList />);
    await screen.findByText('No projects yet');
    expect(container.querySelectorAll('[style]')).toHaveLength(0);
  });
});

describe('opening a project by address', () => {
  it('shows the project detail when the address names one', () => {
    query = { detail: 'p9' };
    render(<ProjectList />);
    expect(screen.getByText('detail of p9')).toBeInTheDocument();
  });
});
