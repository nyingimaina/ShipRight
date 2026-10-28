export interface IBundleWarning {
  severity: 'warning' | 'info';
  code: string;
  location: string;
  message: string;
}

export interface IBundleInspection {
  projectName: string;
  importable: boolean;
  errors: string[];
  warnings: IBundleWarning[];
  counts: Record<string, number>;
}

export interface IImportOutcome {
  projectId: string;
  projectName: string;
  warnings: IBundleWarning[];
}
