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
  /** Protected bundles need the same passphrase at import that was chosen at export. */
  secretsIncluded?: boolean;
  /** Pointers to paths the source machine could not resolve; a destination root makes them portable. */
  requiredInputs?: string[];
  relocatedPaths?: string[];
}

export interface IImportOutcome {
  projectId: string;
  projectName: string;
  warnings: IBundleWarning[];
}
