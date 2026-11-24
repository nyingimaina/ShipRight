/** A variable whose value is different in the server record and the project's copy of the server settings. */
export interface EnvSideDifference {
  name: string;
  serverRecordValue: string;
  projectValue: string;
}

/** Both copies of the server's variables, fetched only when the warning asks for them. */
export interface EnvDivergenceResponse {
  differs: boolean;
  serverFound: boolean;
  variables: EnvSideDifference[];
  serverRecordText: string;
  projectText: string;
}

export type DivergenceCopy = 'to-project' | 'to-server' | 'none';
