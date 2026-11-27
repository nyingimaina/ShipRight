/** Types for the bootstrap endpoints. They mirror the server's records; nothing here is calculated twice. */

export type BootstrapPreset = 'None' | 'ShipRightSelf';

export interface BootstrapServiceInput {
  name: string;
  image: string;
  dockerfile: string;
  context: string;
  target?: string | null;
  args?: Record<string, string> | null;
}

export interface BootstrapRequest {
  preset?: BootstrapPreset;
  projectName?: string;
  repoUrl?: string;
  branch?: string;
  composeFile?: string;
  composeProject?: string;
  services?: BootstrapServiceInput[];
  env?: Record<string, string>;
  serverId?: string;
  serverName?: string;
  host?: string;
  username?: string;
  sshKeyPath?: string;
  remoteWorkingDir?: string;
  domain?: string;
  healthCheckUrl?: string;
  projectId?: string;
  useDeployKey?: boolean;
  credentialResourceId?: string | null;
}

export interface BootstrapStep {
  id: string;
  title: string;
  detail: string;
}

export interface BootstrapPlan {
  server: Record<string, unknown>;
  project: Record<string, unknown>;
  jit: Record<string, unknown>;
  problems: string[];
  warnings: string[];
  deployBlockers: string[];
  deployReady: boolean;
  rules?: {
    ruleId: string;
    title: string;
    scope: string;
    severity: string;
    status: string;
    message: string;
    remediation?: string | null;
  }[];
  steps: BootstrapStep[];
  ok: boolean;
}

export interface BootstrapApplyResult {
  ok: boolean;
  projectId: string | null;
  serverId: string | null;
  publicKey: string | null;
  problems: string[];
  nextSteps: string[];
}

export interface BootstrapApplyResponse {
  result: BootstrapApplyResult;
  plan: BootstrapPlan;
}

export interface GitSuggestedService {
  name: string;
  image: string;
  dockerfile: string;
  context: string;
  target?: string | null;
  args?: Record<string, string> | null;
}

export interface GitInspection {
  url: string;
  provider: string;
  owner: string;
  name: string;
  defaultBranch: string;
  private: boolean;
  repoId: number | null;
  branches: string[];
  composeFiles: string[];
  dockerfiles: string[];
  services: GitSuggestedService[];
  suggestedComposeFile: string | null;
  notes: string[];
}
