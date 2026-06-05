export interface IDetectedService {
  suggestedName: string;
  versionFilePath: string;
  buildContextPath: string;
  dockerImageName: string | null;
  imageDetected: boolean;
}

export interface IDetectedProjectConfig {
  suggestedName: string | null;
  services: IDetectedService[];
  gitRepoPath: string | null;
  deployBranch: string | null;
  wslWorkingDir: string | null;
  detected: string[];
  undetected: string[];
}
