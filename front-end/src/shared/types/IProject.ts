export interface IServiceConfig {
  name: string;
  versionFilePath: string;
  buildContextPath: string;
  dockerImageName: string;
}

export interface IGitConfig {
  repoPath: string;
  deployBranch: string;
}

export interface IWslConfig {
  workingDir: string;
}

export interface IServerConfig {
  host: string;
  username: string;
  sshKeyPath: string;
  remoteWorkingDir: string;
  rebuildScript: string;
}

export interface IProject {
  id: string;
  name: string;
  services: IServiceConfig[];
  gitRepos: IGitConfig[];
  wsl: IWslConfig;
  server: IServerConfig;
  createdAt: string;
  modifiedAt: string;
}

export type IProjectInput = Omit<IProject, 'id' | 'createdAt' | 'modifiedAt'>;

export const emptyProjectInput = (): IProjectInput => ({
  name: '',
  services: [{ name: '', versionFilePath: '', buildContextPath: '', dockerImageName: '' }],
  gitRepos: [],
  wsl: { workingDir: '' },
  server: { host: '', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: 'rebuild.sh' },
});

export interface IApiError {
  isError: boolean;
  field?: string;
  message: string;
}
