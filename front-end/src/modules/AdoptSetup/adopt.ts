import { IProjectInput, IServiceConfig } from '@/shared/types/IProject';

/** Types and pure helpers for adopting a project that already has its own Docker setup. They mirror what the server reports. */

export type Confidence = 'Low' | 'Medium' | 'High';

export interface AdoptedService {
  name: string;
  dockerfilePath: string | null;
  contextRelative: string;
  target: string | null;
  buildArgs: Record<string, string>;
  imageName: string | null;
  composeService: string | null;
  confidence: Confidence;
  alternatives: string[];
  otherStages: string[];
  argsNeedingAValue: string[];
  tagStyle: 'None' | 'Literal' | 'SharedVariable' | 'PerServiceVariable';
  tagVariable: string | null;
  reasons: string[];
  versionFile: string | null;
  suggestedVersionFile: string;
}

export interface AdoptionPlan {
  root: string;
  composeFile: string | null;
  services: AdoptedService[];
  thirdParty: string[];
  unusedDockerfiles: string[];
  warnings: string[];
  repoRoot: string | null;
  repoBranch: string | null;
}

export interface ComposeChange {
  service: string;
  kind: 'image' | 'build';
  before: string;
  after: string;
}

export interface ComposePreview {
  composeFile: string | null;
  changes: ComposeChange[];
  notes: string[];
  newText: string;
}

export type ComposeMode = 'registry' | 'build-on-server';

/** A path under <paramref name="root"/>, written in the same style (Windows or Linux) as the root. */
export function joinPath(root: string, rel: string): string {
  const windows = root.includes('\\') && !root.includes('/');
  const sep = windows ? '\\' : '/';
  const base = root.replace(/[\\/]+$/, '');
  const clean = rel === '.' ? '' : rel.replace(/^[./\\]+/, '').replace(/[\\/]/g, sep);
  return clean ? `${base}${sep}${clean}` : base;
}

const slug = (text: string) => text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'app';

export function suggestAdoptedName(plan: AdoptionPlan): string {
  const folder = plan.root.split(/[\\/]/).filter(Boolean).pop() ?? '';
  const words = folder.replace(/[-_.]+/g, ' ').trim();
  if (!words || /^[A-Za-z]:$/.test(words)) return 'My Project';
  return words.replace(/\b[a-z]/g, ch => ch.toUpperCase());
}

export const needsChoice = (s: AdoptedService) => s.dockerfilePath === null;

export function canCreate(plan: AdoptionPlan, included: Set<string>, name: string): boolean {
  return name.trim() !== '' && plan.services.some(s => included.has(s.name) && s.dockerfilePath !== null);
}

/** A few plain lines about one service, for the review screen. */
export function serviceFacts(s: AdoptedService): string[] {
  const facts: string[] = [];
  if (s.dockerfilePath) facts.push(`Dockerfile: ${s.dockerfilePath}`);
  facts.push(`Built from: ${s.contextRelative === '.' ? 'the project folder' : s.contextRelative}`);
  if (s.target) facts.push(`Stage: ${s.target}`);
  if (s.imageName) facts.push(`Image: ${s.imageName}`);
  return facts;
}

/**
 * The project to save: one service per adopted image, with its real Dockerfile, context, stage and arguments. An ordinary
 * Dockerfile with nothing else to say leaves the recipe empty, so the build command is exactly what it always was.
 * Everything else matches what the guided flow creates.
 */
export function buildAdoptedProjectInput(
  plan: AdoptionPlan, included: Set<string>, argValues: Record<string, Record<string, string>>,
  name: string, timeZone: string, wslHome: string,
): IProjectInput {
  const owner = slug(name);

  const services: IServiceConfig[] = plan.services
    .filter(s => included.has(s.name) && s.dockerfilePath !== null)
    .map(s => {
      const typed = Object.fromEntries(Object.entries(argValues[s.name] ?? {}).filter(([, v]) => v.trim() !== ''));
      const args = { ...s.buildArgs, ...typed };
      const recipe: Partial<IServiceConfig> = {};
      if (s.dockerfilePath !== 'Dockerfile') recipe.dockerfilePath = s.dockerfilePath as string;
      if (s.target) recipe.buildTarget = s.target;
      if (Object.keys(args).length > 0) recipe.buildArgs = args;

      return {
        name: s.name,
        versionFilePath: joinPath(plan.root, s.versionFile ?? s.suggestedVersionFile),
        buildContextPath: joinPath(plan.root, s.contextRelative),
        dockerImageName: s.imageName ?? `${owner}/${slug(s.name)}`,
        dockerRegistry: '',
        composeServiceName: s.composeService ?? slug(s.name),
        localImageKeepCount: 0,
        ...recipe,
      };
    });

  return {
    name,
    type: 'Freeform',
    features: { docker: true, git: true, deploy: false, database: false },
    services,
    gitRepos: [{ repoPath: plan.repoRoot ?? plan.root, deployBranch: plan.repoBranch ?? 'main' }],
    wsl: { workingDir: `${wslHome.replace(/\/+$/, '')}/.shipright/projects/${owner}` },
    server: { host: '', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: '', deployMode: 'EnvCompose' },
    timeZone,
    buildSource: 'CleanSnapshot',
    gitPushTimeoutSeconds: 600,
    localCachePruneKeepGb: 5,
  };
}
