import { buildProjectInput, defaultApprovedIds, describeComponent, describeItem, slug, suggestProjectName } from '../projectFromPlan';
import { IDetectedComponent, IPlanItem, ISetupPlan } from '@/shared/types/ISetup';

function component(over: Partial<IDetectedComponent> = {}): IDetectedComponent {
  return {
    stackId: 'dotnet', kind: 'aspnet-web', name: 'Api',
    directory: 'D:\\work\\shop\\Api', contextDirectory: 'D:\\work\\shop\\Api',
    confidence: 'High', port: 8080, version: null, facts: {}, notes: [], ...over,
  };
}

function item(over: Partial<IPlanItem> = {}): IPlanItem {
  return {
    id: 'Dockerfile:Api/Dockerfile', kind: 'Dockerfile', status: 'WillCreate',
    path: 'D:\\work\\shop\\Api\\Dockerfile', relativePath: 'Api/Dockerfile',
    reason: 'why', content: 'FROM x', componentName: 'Api', ...over,
  };
}

function plan(over: Partial<ISetupPlan> = {}): ISetupPlan {
  return {
    rootPath: 'D:\\work\\shop', repoRoot: 'D:\\work\\shop',
    components: [component()],
    items: [
      item({ id: 'GitRepo:.git', kind: 'GitRepo', path: 'D:\\work\\shop\\.git', relativePath: '.git', componentName: null, content: null }),
      item(),
      item({ id: 'VersionFile:Api/version.txt', kind: 'VersionFile', path: 'D:\\work\\shop\\Api\\version.txt', relativePath: 'Api/version.txt' }),
    ],
    warnings: [], repoBranch: 'main', ...over,
  };
}

describe('slug', () => {
  it('lowercases and joins words with dashes', () => expect(slug('My Cool_App!')).toBe('my-cool-app'));
  it('falls back to "app" when nothing usable remains', () => expect(slug('!!!')).toBe('app'));
  it('trims leading and trailing separators', () => expect(slug('  --Shop--  ')).toBe('shop'));
});

describe('suggestProjectName', () => {
  it('uses the folder name as readable words', () =>
    expect(suggestProjectName(plan({ rootPath: 'D:\\work\\my-cool_shop' }))).toBe('My Cool Shop'));
  it('handles forward slashes and trailing separators', () =>
    expect(suggestProjectName(plan({ rootPath: '/home/me/online-store/' }))).toBe('Online Store'));
  it('falls back to a generic name for a drive root', () =>
    expect(suggestProjectName(plan({ rootPath: 'D:\\' }))).toBe('My Project'));
});

describe('buildProjectInput', () => {
  const input = () => buildProjectInput(plan(), 'Shop', 'Africa/Nairobi', '/home/dev');

  it('creates a Freeform project with just Docker and Git switched on', () => {
    expect(input().type).toBe('Freeform');
    expect(input().features).toEqual({ docker: true, git: true, deploy: false, database: false });
  });

  it('builds from a clean copy of the saved code, so unsaved files can never be built', () => {
    expect(input().buildSource).toBe('CleanSnapshot');
  });

  it('carries the name and time zone', () => {
    expect(input().name).toBe('Shop');
    expect(input().timeZone).toBe('Africa/Nairobi');
  });

  it('turns each component into a service using the planned version file and context', () => {
    const s = input().services[0];
    expect(s.name).toBe('Api');
    expect(s.versionFilePath).toBe('D:\\work\\shop\\Api\\version.txt');
    expect(s.buildContextPath).toBe('D:\\work\\shop\\Api');
    expect(s.composeServiceName).toBe('api');
  });

  it('suggests an owner/app image name that is valid for Docker', () => {
    expect(input().services[0].dockerImageName).toBe('shop/api');
    expect(input().services[0].dockerImageName).toMatch(/^[a-z0-9._\-/]+$/);
  });

  it('points the git repo at the repo root and its branch', () => {
    expect(input().gitRepos).toEqual([{ repoPath: 'D:\\work\\shop', deployBranch: 'main' }]);
  });

  it('falls back to main when the branch is unknown', () => {
    expect(buildProjectInput(plan({ repoBranch: null }), 'Shop', 'UTC', '/home/dev').gitRepos[0].deployBranch).toBe('main');
  });

  it('leaves the server empty so nothing is half-configured', () => {
    expect(input().server.host).toBe('');
  });

  it('skips the compose-repo steps, which a guided project has no use for', () => {
    expect(input().server.deployMode).toBe('EnvCompose');
  });

  it('gives the project its own Linux folder under the WSL home, as the server insists on one', () => {
    expect(input().wsl.workingDir).toBe('/home/dev/.shipright/projects/shop');
  });

  it('keeps that folder name safe whatever the project is called', () => {
    expect(buildProjectInput(plan(), '../../Evil Name!', 'UTC', '/home/dev').wsl.workingDir)
      .toBe('/home/dev/.shipright/projects/evil-name');
  });

  it('names duplicate components apart', () => {
    const p = plan({
      components: [component({ name: 'web' }), component({ name: 'web', contextDirectory: 'D:\\work\\shop\\web2' })],
      items: [
        item({ componentName: 'web', id: 'a', path: 'D:\\work\\shop\\Api\\Dockerfile' }),
        item({ componentName: 'web', id: 'b', kind: 'VersionFile', path: 'D:\\work\\shop\\Api\\version.txt' }),
        item({ componentName: 'web', id: 'c', path: 'D:\\work\\shop\\web2\\Dockerfile' }),
        item({ componentName: 'web', id: 'd', kind: 'VersionFile', path: 'D:\\work\\shop\\web2\\version.txt' }),
      ],
    });
    const names = buildProjectInput(p, 'Shop', 'UTC', '/home/dev').services.map(s => s.name);
    expect(new Set(names).size).toBe(2);
  });

  it('skips components that have no Dockerfile of their own', () => {
    const p = plan({
      components: [component({ name: 'Api' }), component({ name: 'Admin' })],
      items: [
        item({ componentName: 'Api' }),
        item({ id: 'v', kind: 'VersionFile', path: 'D:\\work\\shop\\Api\\version.txt', componentName: 'Api' }),
      ],
    });
    expect(buildProjectInput(p, 'Shop', 'UTC', '/home/dev').services.map(s => s.name)).toEqual(['Api']);
  });
});

describe('defaultApprovedIds', () => {
  it('approves every item that will be created', () => {
    expect(defaultApprovedIds(plan())).toEqual(['GitRepo:.git', 'Dockerfile:Api/Dockerfile', 'VersionFile:Api/version.txt']);
  });

  it('never approves items that already exist', () => {
    const p = plan({ items: [item({ status: 'Exists' }), item({ id: 'x', status: 'WillCreate' })] });
    expect(defaultApprovedIds(p)).toEqual(['x']);
  });
});

describe('plain-language labels', () => {
  it.each([
    ['aspnet-web', 'ASP.NET website or API'],
    ['dotnet-worker', '.NET background service'],
    ['nextjs', 'Next.js website'],
    ['spa', 'Website (React, Vue, Angular…)'],
    ['node-server', 'Node.js server'],
  ])('%s → %s', (kind, label) => expect(describeComponent(component({ kind }))).toBe(label));

  it('falls back to the raw kind for stacks added later', () =>
    expect(describeComponent(component({ kind: 'python-flask' }))).toBe('python-flask'));

  it.each([
    ['GitRepo', 'Git (saves versions of your code)'],
    ['GitIgnore', '.gitignore (keeps secrets out of git)'],
    ['GitAttributes', '.gitattributes (consistent line endings)'],
    ['Dockerfile', 'Dockerfile (how to build it)'],
    ['DockerIgnore', '.dockerignore (what to leave out)'],
    ['VersionFile', 'version.txt (its version number)'],
  ] as const)('%s → %s', (kind, label) => expect(describeItem(item({ kind }))).toBe(label));
});
