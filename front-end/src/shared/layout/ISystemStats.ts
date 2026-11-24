/** The cheap, names-free host snapshot shown in the global header. */
export interface ISystemStats {
  diskMount: string;
  diskUsedPercent: number;
  diskUsedBytes: number;
  diskTotalBytes: number;
  ramUsedPercent: number;
  ramUsedBytes: number;
  ramTotalBytes: number;
  processCpuPercent: number;
  processCount: number;
  available: boolean;
  capturedAtUtc: string;
  error?: string | null;
}
