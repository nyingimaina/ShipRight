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

export type DeployMode = 'Unmanaged' | 'SemiManaged' | 'FullyManaged';

export interface IServerConfig {
  host: string;
  username: string;
  sshKeyPath: string;
  remoteWorkingDir: string;
  rebuildScript: string;
  deployMode: DeployMode;
}

export type DbProviderType = 'MariaDb' | 'SqlServer';

export interface IDatabaseConfig {
  provider: DbProviderType;
  containerName: string;
  databaseName: string;
  rootUser: string;
  backupRetainCount: number;
}

export interface IProject {
  id: string;
  name: string;
  services: IServiceConfig[];
  gitRepos: IGitConfig[];
  wsl: IWslConfig;
  server: IServerConfig;
  database?: IDatabaseConfig;
  createdAt: string;
  modifiedAt: string;
}

export type IProjectInput = Omit<IProject, 'id' | 'createdAt' | 'modifiedAt'>;

export const emptyDatabaseConfig = (): IDatabaseConfig => ({
  provider: 'MariaDb',
  containerName: '',
  databaseName: '',
  rootUser: 'root',
  backupRetainCount: 10,
});

export const emptyProjectInput = (): IProjectInput => ({
  name: '',
  services: [{ name: '', versionFilePath: '', buildContextPath: '', dockerImageName: '' }],
  gitRepos: [],
  wsl: { workingDir: '' },
  server: { host: '', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: 'rebuild.sh', deployMode: 'Unmanaged' },
});

export interface IApiError {
  isError: boolean;
  field?: string;
  message: string;
}
