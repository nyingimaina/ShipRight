export interface IDetectedService {
  suggestedName: string;
  versionFilePath: string;
  buildContextPath: string;
  dockerImageName: string | null;
  imageDetected: boolean;
}

export interface IDetectedGitRepo {
  repoPath: string;
  deployBranch: string;
}

export interface IDetectedProjectConfig {
  suggestedName: string | null;
  services: IDetectedService[];
  gitRepos: IDetectedGitRepo[];
  wslWorkingDir: string | null;
  detected: string[];
  undetected: string[];
}
