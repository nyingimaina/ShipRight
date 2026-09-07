export interface IServiceConfig {
  name: string;
  versionFilePath: string;
  buildContextPath: string;
  dockerImageName: string;
  dockerRegistry?: string;
  composeServiceName: string;
  dockerUsername?: string;
  dockerPassword?: string;
  dockerRegistryResourceId?: string;
  imageRetentionCount?: number;
  localImageKeepCount?: number;
}

export interface IGitConfig {
  repoPath: string;
  deployBranch: string;
  pushArgs?: string;
  credentialResourceId?: string;
}

export interface IWslConfig {
  workingDir: string;
}

export type DeployMode = 'GitScript' | 'GitCompose' | 'EnvCompose';

export type ProjectType = 'Pipeline' | 'Freeform';

export interface IFreeformFeatures {
  docker: boolean;
  git: boolean;
  deploy: boolean;
  database: boolean;
}

export type ScriptPlatform = 'Bash' | 'PowerShell' | 'Cmd' | 'Python' | 'Sh';
export type ExecutionTarget = 'Local' | 'Remote';
export type PipelineScope = 'Global' | 'Project';
export type PipelineStepType = 'Script' | 'Build' | 'Push' | 'Deploy';

export interface IServerConfig {
  id?: string;
  name?: string;
  host: string;
  username: string;
  sshKeyPath: string;
  remoteWorkingDir: string;
  rebuildScript: string;
  deployMode: DeployMode;
  managedSshKey?: boolean;
  rebuildScriptResourceId?: string;
  pipelineResourceId?: string;
}

export type DbProviderType = 'MariaDb' | 'SqlServer';

export interface IDatabaseConfig {
  provider: DbProviderType;
  containerName: string;
  databaseName: string;
  rootUser: string;
  backupRetainCount: number;
  rootPassword?: string;
}

export interface IProject {
  id: string;
  name: string;
  version?: number;
  serverId?: string;
  type?: ProjectType;
  features?: IFreeformFeatures;
  services: IServiceConfig[];
  gitRepos: IGitConfig[];
  wsl: IWslConfig;
  server: IServerConfig;
  database?: IDatabaseConfig;
  watchBranch?: string;
  watchPollSeconds?: number;
  watchSteps?: string;
gitPushTimeoutSeconds?: number;
  timeZone?: string;
  localCachePruneKeepGb?: number;
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
  type: 'Pipeline',
  features: { docker: false, git: false, deploy: false, database: false },
  services: [{ name: '', versionFilePath: '', buildContextPath: '', dockerImageName: '', dockerRegistry: '', composeServiceName: '', dockerUsername: '', dockerPassword: '' }],
  gitRepos: [],
  wsl: { workingDir: '' },
  server: { host: '', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: 'rebuild.sh', deployMode: 'GitScript' },
  gitPushTimeoutSeconds: 600,
  timeZone: 'UTC',
  localCachePruneKeepGb: 5,
});

export interface IApiError {
  isError: boolean;
  field?: string;
  message: string;
}

export type RegistryAuthType = 'Password' | 'AwsEcr';

export interface IDockerRegistryResource {
  id: string;
  name: string;
  registry: string;
  username: string;
  password?: string;
  authType?: RegistryAuthType;
  awsRegion?: string;
  awsProfileResourceId?: string;
  tags?: string[];
  createdAt: string;
  modifiedAt: string;
}

export interface IAwsProfileResource {
  id: string;
  name: string;
  profileName?: string;
  accessKeyId?: string;
  secretAccessKey?: string;
  sessionToken?: string;
  defaultRegion?: string;
  tags?: string[];
  createdAt: string;
  modifiedAt: string;
}

export interface IScriptResource {
  id: string;
  name: string;
  content: string;
  platform: ScriptPlatform;
  target: ExecutionTarget;
  scope: PipelineScope;
  projectId?: string;
  workingDirectory?: string;
  variables?: Record<string, string>;
  createdAt: string;
  modifiedAt: string;
}

export interface ICredentialResource {
  id: string;
  name: string;
  value?: string;
  hostPattern?: string;
  projectId?: string;
  tags?: string[];
  createdAt: string;
  modifiedAt: string;
}

export interface IPipelineStep {
  id: string;
  type: PipelineStepType;
  scriptResourceId?: string;
  deployMode?: DeployMode;
  label?: string;
  workingDirectory?: string;
  continueOnError?: boolean;
}

export interface IPipelineResource {
  id: string;
  name: string;
  steps: IPipelineStep[];
  scope: PipelineScope;
  projectId?: string;
  variables?: Record<string, string>;
  createdAt: string;
  modifiedAt: string;
}

export type AwsCredentialsSourceKind = 'None' | 'Wsl' | 'Native';

export interface IAwsCredentialProfile {
  name: string;
  region: string | null;
  hasKeys: boolean;
  unsupportedReason: string | null;
}

export interface IAwsCredentialsSource {
  kind: AwsCredentialsSourceKind;
  fileExists: boolean;
  profiles: IAwsCredentialProfile[];
}

export type AwsValidationErrorCode =
  | 'aws-cli-missing'
  | 'profile-not-found'
  | 'invalid-credentials'
  | 'expired-token'
  | 'denied'
  | 'network'
  | 'no-region'
  | 'empty-output';

export interface IAwsCliInstallRequest {
  strategy?: 'auto' | 'pip';
}

export interface IAwsCliInstallResult {
  success: boolean;
  message: string;
  requiresManualInstall?: boolean;
  command?: string | null;
  outputTail?: string | null;
}

export interface IAwsProfileValidationResult {
  ok: boolean;
  accountId?: string | null;
  arn?: string | null;
  errorCode?: AwsValidationErrorCode | null;
  message?: string | null;
  hint?: string | null;
}

export interface IAwsProfileValidateRequest {
  profileId?: string | null;
  profile?: {
    name?: string;
    profileName?: string;
    accessKeyId?: string;
    secretAccessKey?: string;
    sessionToken?: string;
    defaultRegion?: string;
  } | null;
}
