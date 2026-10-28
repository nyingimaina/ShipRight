export type SetupConfidence = 'Low' | 'Medium' | 'High';

export interface IDetectedComponent {
  stackId: string;
  kind: string;
  name: string;
  directory: string;
  contextDirectory: string;
  confidence: SetupConfidence;
  port: number | null;
  version: string | null;
  facts: Record<string, string>;
  notes: string[];
}

export type PlanItemKind = 'GitRepo' | 'GitIgnore' | 'GitAttributes' | 'Dockerfile' | 'DockerIgnore' | 'VersionFile';
export type PlanItemStatus = 'Exists' | 'WillCreate';

export interface IPlanItem {
  id: string;
  kind: PlanItemKind;
  status: PlanItemStatus;
  path: string;
  relativePath: string;
  reason: string;
  content: string | null;
  componentName: string | null;
}

export interface ISetupPlan {
  rootPath: string;
  repoRoot: string;
  components: IDetectedComponent[];
  items: IPlanItem[];
  warnings: string[];
  repoBranch: string | null;
}

export type ApplyOutcome = 'Created' | 'SkippedExists' | 'SkippedNotApproved' | 'Failed';

export interface IApplyItemResult {
  itemId: string;
  outcome: ApplyOutcome;
  message: string;
}

export interface IDirtyEntry {
  status: string;
  path: string;
  description: string;
}

export interface ICleanTreeResult {
  isClean: boolean;
  sha: string | null;
  entries: IDirtyEntry[];
  error: string | null;
}

export interface IApplyResult {
  items: IApplyItemResult[];
  committed: boolean;
  commitError: string | null;
}
