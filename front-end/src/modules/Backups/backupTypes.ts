import { formatDate as formatInUserZone, formatDateTime as formatDateTimeInUserZone } from '@/shared/time';

export interface BackupFile {
  fileName: string;
  databaseName: string;
  format: string;
  compression: string;
  compressionLevel: number;
  encryption: string;
  keyId: string;
  originalBytes: number;
  storedBytes: number;
  ratio: number;
  sha256: string;
  createdAt: string;
  source: 'manual' | 'schedule' | string;
  scheduleId: string | null;
  durationMs: number;
  rank: number;
  projectedDeleteAt: string | null;
}

export interface BackupListing {
  files: BackupFile[];
  keepCount: number;
  keepDays: number;
  cadenceSeconds: number | null;
}

export interface BackupStorageInfo {
  root: string;
  insideDataDirectory: boolean;
  availableBytes: number;
  usedBytes: number;
  count: number;
  oldest: string | null;
  newest: string | null;
}

export function fmtBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

export function fmtWhen(iso: string): string {
  const date = new Date(iso);
  const seconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  if (seconds < 86400 * 30) return `${Math.floor(seconds / 86400)}d ago`;
  return formatInUserZone(date);
}

export function fmtDate(iso: string | null): string {
  return iso ? formatInUserZone(iso) : '—';
}

export function fmtExact(iso: string): string {
  return formatDateTimeInUserZone(iso);
}

/** "kept #2 of 10 (roughly 5 Dec)" — honest about count-based retention being an estimate. */
export function retentionLabel(file: BackupFile, keepCount: number, keepDays: number): string {
  const parts: string[] = [`#${file.rank} of ${keepCount}`];
  if (file.projectedDeleteAt) parts.push(`~${fmtDate(file.projectedDeleteAt)}`);
  else if (keepDays > 0) parts.push(`age limit ${keepDays}d`);
  return parts.join(' · ');
}
