import * as signalR from '@microsoft/signalr';
import { api } from './ApiService';
import { IBuildRecord } from './types/IBuildRecord';

export interface SignalRHandlers {
  onLogLine?: (e: { buildId: string; source: string; line: string; timestamp: string }) => void;
  onStepStarted?: (e: { buildId: string; stepNumber: number; stepName: string }) => void;
  onStepCompleted?: (e: { buildId: string; stepNumber: number; stepName: string; success: boolean }) => void;
  onPauseRequested?: (e: { buildId: string; reason: string; prompt: string; options: string[]; fields?: string[] }) => void;
  onBuildCompleted?: (e: { buildId: string; status: string; gitTag?: string }) => void;
  onDeployCompleted?: (e: { buildId: string; status: string }) => void;
  onConnectionChange?: (state: 'connected' | 'reconnecting' | 'disconnected') => void;
}

class BuildSignalRService {
  private connection: signalR.HubConnection | null = null;
  private activeBuildId: string | null = null;

  async connect(buildId: string, handlers: SignalRHandlers): Promise<void> {
    await this.disconnect();

    const base = process.env.NEXT_PUBLIC_API_URL ?? '';
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${base}/hubs/build`)
      .withAutomaticReconnect([1000, 2000, 4000, 8000, 30000])
      .build();

    if (handlers.onLogLine)       this.connection.on('LogLine',        handlers.onLogLine);
    if (handlers.onStepStarted)   this.connection.on('StepStarted',    handlers.onStepStarted);
    if (handlers.onStepCompleted) this.connection.on('StepCompleted',  handlers.onStepCompleted);
    if (handlers.onPauseRequested)this.connection.on('PauseRequested', handlers.onPauseRequested);
    if (handlers.onBuildCompleted)this.connection.on('BuildCompleted', handlers.onBuildCompleted);
    if (handlers.onDeployCompleted)this.connection.on('DeployCompleted',handlers.onDeployCompleted);

    this.connection.onreconnecting(() => handlers.onConnectionChange?.('reconnecting'));
    this.connection.onreconnected(async () => {
      handlers.onConnectionChange?.('connected');
      if (this.activeBuildId) {
        await this.connection?.invoke('JoinBuild', this.activeBuildId);
        // Catch up on missed log lines
        try {
          const record = await api.get<IBuildRecord>(`/api/builds/${this.activeBuildId}`);
          handlers.onBuildCompleted?.({ buildId: this.activeBuildId!, status: record.status });
        } catch {}
      }
    });
    this.connection.onclose(() => handlers.onConnectionChange?.('disconnected'));

    await this.connection.start();
    await this.connection.invoke('JoinBuild', buildId);
    this.activeBuildId = buildId;
    handlers.onConnectionChange?.('connected');
  }

  async disconnect(): Promise<void> {
    if (this.connection) {
      try {
        if (this.activeBuildId) await this.connection.invoke('LeaveBuild', this.activeBuildId);
        await this.connection.stop();
      } catch {}
      this.connection = null;
      this.activeBuildId = null;
    }
  }
}

export const buildSignalR = new BuildSignalRService();
