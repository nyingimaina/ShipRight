// Response from GET /api/projects/{id}/current-versions
export interface IServiceVersion {
  serviceName: string;
  version: string | null;
  suggestedNext: string | null;
  versionFilePath: string;
  error: string | null;
}

// Service version inside a BuildRecord
export interface IBuildServiceVersion {
  serviceName: string;
  previousVersion: string;
  newVersion: string;
  dockerImageName: string;
}

export type BuildStatus =
  | 'Pending' | 'Running' | 'Paused'
  | 'BuildSucceeded' | 'BuildFailed' | 'Aborted' | 'Interrupted'
  | 'Deploying' | 'Deployed' | 'DeployFailed';

export interface IBuildRecord {
  id: string;
  projectId: string;
  projectName: string;
  status: BuildStatus;
  gitTag: string;
  versions: IBuildServiceVersion[];
  startedAt: string;
  completedAt: string | null;
  deployedAt: string | null;
  failedStep: string | null;
  currentStepNumber: number | null;
  currentStepName: string | null;
  logOutput: string;
  errorSummary: string | null;
}

export interface IBuildListResponse {
  items: IBuildRecord[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}
