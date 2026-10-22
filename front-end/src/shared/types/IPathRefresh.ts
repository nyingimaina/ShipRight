import { IProject } from './IProject';

export interface IPathChange {
  serviceName: string;
  oldVersionFilePath: string;
  newVersionFilePath: string;
  oldBuildContextPath: string;
  newBuildContextPath: string;
}

export interface IPathRefreshPlan {
  changes: IPathChange[];
  notFoundInFolder: string[];
  newInFolder: string[];
}

export interface IPathRefreshResponse {
  plan: IPathRefreshPlan;
  project: IProject | null;
}
