import { IProjectInput } from '@/shared/types/IProject';
import { IDetectedComponent, IPlanItem, ISetupPlan, PlanItemKind } from '@/shared/types/ISetup';

const COMPONENT_LABELS: Record<string, string> = {
  'aspnet-web': 'ASP.NET website or API',
  'dotnet-worker': '.NET background service',
  nextjs: 'Next.js website',
  spa: 'Website (React, Vue, Angular…)',
  'node-server': 'Node.js server',
};

const ITEM_LABELS: Record<PlanItemKind, string> = {
  GitRepo: 'Git (saves versions of your code)',
  GitIgnore: '.gitignore (keeps secrets out of git)',
  GitAttributes: '.gitattributes (consistent line endings)',
  Dockerfile: 'Dockerfile (how to build it)',
  DockerIgnore: '.dockerignore (what to leave out)',
  VersionFile: 'version.txt (its version number)',
};

export const describeComponent = (c: IDetectedComponent) => COMPONENT_LABELS[c.kind] ?? c.kind;

export const describeItem = (i: IPlanItem) => ITEM_LABELS[i.kind];

export function slug(text: string): string {
  return text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'app';
}

/** The folder name as readable words: "my-cool_shop" → "My Cool Shop". */
export function suggestProjectName(plan: ISetupPlan): string {
  const folder = plan.rootPath.split(/[\\/]/).filter(Boolean).pop() ?? '';
  const words = folder.replace(/[-_.]+/g, ' ').trim();
  if (!words || /^[A-Za-z]:$/.test(words)) return 'My Project';
  return words.replace(/\b[a-z]/g, ch => ch.toUpperCase());
}

/** Everything that will be created is pre-ticked; the person can untick what they do not want. */
export const defaultApprovedIds = (plan: ISetupPlan): string[] =>
  plan.items.filter(i => i.status === 'WillCreate').map(i => i.id);

const folderOf = (path: string) => path.replace(/[\\/][^\\/]*$/, '').replace(/\\/g, '/').toLowerCase();

/**
 * The service a component becomes, if it has a Dockerfile of its own. The version file sits next to the
 * Dockerfile (the build context), which is how ShipRight pairs them.
 */
function ownedItem(plan: ISetupPlan, c: IDetectedComponent, kind: PlanItemKind): IPlanItem | undefined {
  return plan.items.find(i =>
    i.kind === kind && i.componentName === c.name && folderOf(i.path) === c.contextDirectory.replace(/\\/g, '/').toLowerCase());
}

/**
 * @param wslHome the person's home folder inside WSL; the project gets a ShipRight-owned folder beneath it
 *                because every project with services must have a Linux working directory.
 */
export function buildProjectInput(plan: ISetupPlan, name: string, timeZone: string, wslHome: string): IProjectInput {
  const owner = slug(name);
  const used = new Set<string>();

  const services = plan.components.flatMap(c => {
    const dockerfile = ownedItem(plan, c, 'Dockerfile');
    const version = ownedItem(plan, c, 'VersionFile');
    if (!dockerfile || !version) return [];

    let serviceName = c.name;
    for (let n = 2; used.has(serviceName.toLowerCase()); n++) serviceName = `${c.name} ${n}`;
    used.add(serviceName.toLowerCase());

    return [{
      name: serviceName,
      versionFilePath: version.path,
      buildContextPath: c.contextDirectory,
      dockerImageName: `${owner}/${slug(serviceName)}`,
      dockerRegistry: '',
      composeServiceName: slug(serviceName),
    }];
  });

  return {
    name,
    type: 'Freeform',
    features: { docker: true, git: true, deploy: false, database: false },
    services,
    gitRepos: [{ repoPath: plan.repoRoot, deployBranch: plan.repoBranch ?? 'main' }],
    wsl: { workingDir: `${wslHome.replace(/\/+$/, '')}/.shipright/projects/${owner}` },
    // EnvCompose: image tags are injected at deploy time, so no compose repo is needed to build.
    server: { host: '', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: '', deployMode: 'EnvCompose' },
    timeZone,
    buildSource: 'CleanSnapshot',
    gitPushTimeoutSeconds: 600,
    localCachePruneKeepGb: 5,
  };
}
