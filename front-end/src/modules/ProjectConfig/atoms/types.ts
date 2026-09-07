import { DbProviderType, DeployMode, ProjectType } from '@/shared/types/IProject';

export interface ServiceDraft {
  name: string;
  versionFilePath: string;
  buildContextPath: string;
  dockerImageName: string;
  dockerRegistry: string;
  dockerRegistryResourceId?: string;
  composeServiceName: string;
  dockerUsername: string;
  dockerPassword: string;
  imageRetentionCount?: number;
  localImageKeepCount?: number;
  version: string | null;
}

export interface GitRepoDraft {
  repoPath: string;
  deployBranch: string;
  pushArgs?: string;
  credentialResourceId?: string;
}

export interface ProjectDraft {
  name: string;
  timeZone: string;
  type: ProjectType;
  features: { docker: boolean; git: boolean; deploy: boolean; database: boolean };
  services: ServiceDraft[];
  gitRepos: GitRepoDraft[];
  wslWorkingDir: string;
  server: {
    host: string;
    username: string;
    sshKeyPath: string;
    remoteWorkingDir: string;
    rebuildScript: string;
    deployMode: DeployMode;
  };
  serverId: string;
  databaseEnabled: boolean;
  database: {
    provider: DbProviderType;
    containerName: string;
    databaseName: string;
    rootUser: string;
    backupRetainCount: number;
    rootPassword: string;
  };
  watchBranch: string;
  watchPollSeconds: number;
  watchSteps: string;
  gitPushTimeoutSeconds: number;
  localCachePruneKeepGb: number;
}

export const emptyServiceDraft = (): ServiceDraft => ({
  name: '',
  versionFilePath: '',
  buildContextPath: '',
  dockerImageName: '',
  dockerRegistry: '',
  composeServiceName: '',
  dockerUsername: '',
  dockerPassword: '',
  imageRetentionCount: 5,
  localImageKeepCount: 2,
  version: null,
});

export const emptyGitRepoDraft = (): GitRepoDraft => ({
  repoPath: '',
  deployBranch: 'master',
});
