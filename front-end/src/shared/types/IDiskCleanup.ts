export interface ICleanupImage {
  reference: string;
  id: string;
  size: string;
  sizeBytes: number;
  age: string;
  owner: 'ShipRight' | 'Other';
  selectedByDefault: boolean;
}

export interface ICleanupPreview {
  images: ICleanupImage[];
  inUseCount: number;
  danglingCount: number;
  selectedBytes: number;
  diskUsage: string;
  error?: string | null;
}

export interface ICleanupItemResult {
  reference: string;
  removed: boolean;
  note?: string | null;
}

export interface ICleanupResult {
  items: ICleanupItemResult[];
  danglingOutput?: string | null;
  buildCacheOutput?: string | null;
  before: string;
  after: string;
}

export interface IWslDiskReport {
  vhdxFiles: { path: string; sizeBytes: number }[];
  distros: { name: string; state: string }[];
  dockerDfSummary?: string | null;
  error?: string | null;
}

export interface IWslShrinkResult {
  succeeded: boolean;
  blockedReason?: string | null;
  files: { path: string; beforeBytes: number; afterBytes: number }[];
  messages: string[];
}

export interface IActiveBuild {
  id: string;
  projectName: string;
  status: 'Running' | 'Paused' | 'Pending';
  step?: string | null;
  startedAt: string;
  canAbort: boolean;
}

export type DiskStepStatus = 'Pending' | 'Running' | 'Done' | 'Failed' | 'Skipped';
export type DiskJobStatus = 'Running' | 'Succeeded' | 'Failed' | 'Blocked';

export interface IDiskJobStep {
  id: string;
  label: string;
  status: DiskStepStatus;
  startedAt?: string | null;
  finishedAt?: string | null;
  estimatedSeconds: number;
  detail?: string | null;
}

export interface IDiskJob {
  id: string;
  status: DiskJobStatus;
  startedAt: string;
  finishedAt?: string | null;
  steps: IDiskJobStep[];
  blockers: IActiveBuild[];
  cleanup?: ICleanupResult | null;
  shrink?: IWslShrinkResult | null;
  error?: string | null;
}

export interface IDiskJobRequest {
  images: string[];
  pruneDangling: boolean;
  pruneBuildCache: boolean;
  shrink: boolean;
}

export type CheckStatus = 'Ok' | 'Warning' | 'Problem' | 'Skipped';

export interface ISystemCheckItem {
  id: string;
  title: string;
  status: CheckStatus;
  detail: string;
  fixId?: string | null;
  fixLabel?: string | null;
  manualSteps?: string | null;
}

export interface ISystemCheckReport {
  items: ISystemCheckItem[];
  healthy: boolean;
}

export interface ISystemFixResult {
  succeeded: boolean;
  message: string;
  output: string;
  report: ISystemCheckReport;
}
