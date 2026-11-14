import {
  AdoptedService, AdoptionPlan, buildAdoptedProjectInput, canCreate, joinPath, needsChoice, serviceFacts, suggestAdoptedName,
} from '../adopt';

const svc = (over: Partial<AdoptedService> = {}): AdoptedService => ({
  name: 'backend', dockerfilePath: 'backend.Dockerfile', contextRelative: '.', target: null, buildArgs: {},
  imageName: '825.dkr.ecr.eu-north-1.amazonaws.com/herd-backend', composeService: 'backend', confidence: 'High',
  alternatives: [], otherStages: [], argsNeedingAValue: [], tagStyle: 'SharedVariable', tagVariable: 'IMAGE_TAG', reasons: [],
  versionFile: 'back-end/version.txt', suggestedVersionFile: 'back-end/version.txt', ...over,
});

const plan = (over: Partial<AdoptionPlan> = {}): AdoptionPlan => ({
  root: 'D:\\work\\herd', composeFile: 'docker-compose.yml', services: [svc(), svc({ name: 'frontend', dockerfilePath: 'frontend.Dockerfile', composeService: 'frontend', imageName: '825.dkr.ecr.eu-north-1.amazonaws.com/herd-frontend', versionFile: null, suggestedVersionFile: 'front-end/version.txt' })],
  thirdParty: ['db'], unusedDockerfiles: ['back-end/dockerfile'], warnings: [], repoRoot: 'D:\\work\\herd', repoBranch: 'master', ...over,
});

describe('joinPath', () => {
  it('writes the path in the style of the folder it is under', () => {
    expect(joinPath('D:\\work\\herd', 'back-end/version.txt')).toBe('D:\\work\\herd\\back-end\\version.txt');
    expect(joinPath('/home/u/herd', 'back-end/version.txt')).toBe('/home/u/herd/back-end/version.txt');
    expect(joinPath('D:\\work\\herd\\', 'a')).toBe('D:\\work\\herd\\a');
  });

  it('treats the folder itself as "."', () => {
    expect(joinPath('D:\\work\\herd', '.')).toBe('D:\\work\\herd');
    expect(joinPath('/srv/app/', '')).toBe('/srv/app');
  });
});

describe('buildAdoptedProjectInput', () => {
  const build = (p = plan(), include = new Set(['backend', 'frontend']), values: Record<string, Record<string, string>> = {}) =>
    buildAdoptedProjectInput(p, include, values, 'Herd IQ', 'Africa/Nairobi', '/home/u');

  it('makes one service per adopted image, with the real paths, image names and compose names', () => {
    const input = build();
    expect(input.services.map(s => s.name)).toEqual(['backend', 'frontend']);
    const be = input.services[0];
    expect(be.buildContextPath).toBe('D:\\work\\herd');
    expect(be.versionFilePath).toBe('D:\\work\\herd\\back-end\\version.txt');
    expect(be.dockerImageName).toBe('825.dkr.ecr.eu-north-1.amazonaws.com/herd-backend');
    expect(be.composeServiceName).toBe('backend');
    expect(be.dockerfilePath).toBe('backend.Dockerfile');
    expect(input.services[1].versionFilePath).toBe('D:\\work\\herd\\front-end\\version.txt');
  });

  it('only includes the services that were ticked, and never one with no Dockerfile decided', () => {
    expect(build(plan(), new Set(['frontend'])).services.map(s => s.name)).toEqual(['frontend']);
    const undecided = plan({ services: [svc({ dockerfilePath: null, confidence: 'Low' })] });
    expect(build(undecided, new Set(['backend'])).services).toEqual([]);
  });

  it('leaves the recipe empty for an ordinary Dockerfile, so the old command is unchanged', () => {
    const plain = plan({ services: [svc({ dockerfilePath: 'Dockerfile', contextRelative: 'api' })] });
    const s = build(plain, new Set(['backend'])).services[0];
    expect(s.dockerfilePath).toBeUndefined();
    expect(s.buildTarget).toBeUndefined();
    expect(s.buildArgs).toBeUndefined();
    expect(s.buildContextPath).toBe('D:\\work\\herd\\api');
  });

  it('carries a build stage and the arguments, including the values typed in', () => {
    const p = plan({ services: [svc({ target: 'runner', buildArgs: { A: '1' }, argsNeedingAValue: ['API_URL'] })] });
    const s = build(p, new Set(['backend']), { backend: { API_URL: 'https://x.example' } }).services[0];
    expect(s.buildTarget).toBe('runner');
    expect(s.buildArgs).toEqual({ A: '1', API_URL: 'https://x.example' });
  });

  it('leaves out an argument that was given no value', () => {
    const p = plan({ services: [svc({ argsNeedingAValue: ['API_URL'] })] });
    expect(build(p, new Set(['backend']), { backend: { API_URL: '  ' } }).services[0].buildArgs).toBeUndefined();
  });

  it('names an image after the project when the compose file did not', () => {
    const p = plan({ services: [svc({ imageName: null, composeService: null })] });
    const s = build(p, new Set(['backend'])).services[0];
    expect(s.dockerImageName).toBe('herd-iq/backend');
    expect(s.composeServiceName).toBe('backend');
  });

  it('builds the same kind of project the guided flow does', () => {
    const input = build();
    expect(input.type).toBe('Freeform');
    expect(input.buildSource).toBe('CleanSnapshot');
    expect(input.server.deployMode).toBe('EnvCompose');
    expect(input.gitRepos).toEqual([{ repoPath: 'D:\\work\\herd', deployBranch: 'master' }]);
    expect(input.wsl.workingDir).toBe('/home/u/.shipright/projects/herd-iq');
    expect(input.services.every(s => s.localImageKeepCount === 0)).toBe(true);
  });

  it('falls back to main when the branch is unknown', () => {
    expect(build(plan({ repoBranch: null })).gitRepos[0].deployBranch).toBe('main');
  });
});

describe('canCreate', () => {
  it('needs a name and at least one decided, ticked service', () => {
    expect(canCreate(plan(), new Set(['backend']), 'Herd')).toBe(true);
    expect(canCreate(plan(), new Set(), 'Herd')).toBe(false);
    expect(canCreate(plan(), new Set(['backend']), '  ')).toBe(false);
    expect(canCreate(plan({ services: [svc({ dockerfilePath: null })] }), new Set(['backend']), 'Herd')).toBe(false);
  });
});

describe('needsChoice and serviceFacts', () => {
  it('asks for a choice only when no Dockerfile was decided', () => {
    expect(needsChoice(svc())).toBe(false);
    expect(needsChoice(svc({ dockerfilePath: null, alternatives: ['a/Dockerfile', 'b/Dockerfile'] }))).toBe(true);
  });

  it('describes a service in plain words', () => {
    const f = serviceFacts(svc());
    expect(f).toContain('Dockerfile: backend.Dockerfile');
    expect(f).toContain('Built from: the project folder');
    expect(f).toContain('Image: 825.dkr.ecr.eu-north-1.amazonaws.com/herd-backend');
    expect(serviceFacts(svc({ contextRelative: 'api', target: 'runner' }))).toEqual(expect.arrayContaining(['Built from: api', 'Stage: runner']));
  });
});

describe('suggestAdoptedName', () => {
  it('turns the folder name into words', () => {
    expect(suggestAdoptedName(plan({ root: 'D:\\work\\savanna-herd-iq' }))).toBe('Savanna Herd Iq');
    expect(suggestAdoptedName(plan({ root: '/home/u/my_app/' }))).toBe('My App');
    expect(suggestAdoptedName(plan({ root: 'D:\\' }))).toBe('My Project');
  });
});
