/** A variable defined on both the server and the project. Values are only ever fetched by the explicit deploy-time check. */
export interface EnvDuplicate {
  name: string;
  serverValue: string;
  projectValue: string;
}

export interface EnvDuplicatesResponse {
  duplicates: EnvDuplicate[];
  skipped: boolean;
  note: string | null;
}

export type DuplicateChoice = 'server' | 'project' | 'none';
