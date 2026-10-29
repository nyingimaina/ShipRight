export interface ISpaceImage {
  reference: string;
  id: string;
  size: string;
  sizeBytes: number;
  age: string;
  inUse: boolean;
  origin: 'Successful build' | 'Failed build' | 'Unknown';
  buildVersion?: string | null;
}

export interface ISpaceFolder {
  buildId: string;
  path: string;
  sizeBytes: number;
  active: boolean;
  origin: 'Successful build' | 'Failed build' | 'Unknown';
}

export interface ISpaceService {
  name: string;
  keepCount: number;
  neverClean: boolean;
}

export interface IProjectSpaceReport {
  projectId: string;
  images: ISpaceImage[];
  snapshots: ISpaceFolder[];
  imagesBytes: number;
  snapshotsBytes: number;
  cacheBytes: number;
  totalBytes: number;
  reclaimableBytes: number;
  services: ISpaceService[];
  deletesAfterPush: boolean;
  error?: string | null;
}

export interface ISpaceCleanResult {
  items: { name: string; kind: string; removed: boolean; note?: string | null }[];
  freedBytes: number;
  report?: IProjectSpaceReport | null;
}
