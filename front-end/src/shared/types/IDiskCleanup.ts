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
