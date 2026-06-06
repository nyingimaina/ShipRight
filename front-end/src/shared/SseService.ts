import { api } from './ApiService';
import { IBuildRecord } from './types/IBuildRecord';

export interface SseHandlers {
  onLogLine?: (e: { buildId: string; source: string; line: string; timestamp: string }) => void;
  onStepStarted?: (e: { buildId: string; stepNumber: number; stepName: string }) => void;
  onStepCompleted?: (e: { buildId: string; stepNumber: number; stepName: string; success: boolean }) => void;
  onPauseRequested?: (e: { buildId: string; reason: string; prompt: string; options: string[]; fields?: string[] }) => void;
  onBuildCompleted?: (e: { buildId: string; status: string; gitTag?: string }) => void;
  onPushCompleted?: (e: { buildId: string; status: string }) => void;
  onDeployCompleted?: (e: { buildId: string; status: string }) => void;
  onConnectionChange?: (state: 'connected' | 'reconnecting' | 'disconnected') => void;
}

class BuildSseService {
  private es: EventSource | null = null;
  private activeBuildId: string | null = null;
  private handlers: SseHandlers = {};

  connect(buildId: string, handlers: SseHandlers): void {
    this.disconnect();
    this.handlers = handlers;
    this.activeBuildId = buildId;

    const base = process.env.NEXT_PUBLIC_API_URL ?? '';
    this.es = new EventSource(`${base}/api/builds/${buildId}/stream`);

    this.es.onopen = () => handlers.onConnectionChange?.('connected');

    this.es.onmessage = (e: MessageEvent) => {
      try {
        const { type, data } = JSON.parse(e.data) as { type: string; data: unknown };
        switch (type) {
          case 'LogLine':        handlers.onLogLine?.(data as Parameters<NonNullable<SseHandlers['onLogLine']>>[0]); break;
          case 'StepStarted':    handlers.onStepStarted?.(data as Parameters<NonNullable<SseHandlers['onStepStarted']>>[0]); break;
          case 'StepCompleted':  handlers.onStepCompleted?.(data as Parameters<NonNullable<SseHandlers['onStepCompleted']>>[0]); break;
          case 'PauseRequested': handlers.onPauseRequested?.(data as Parameters<NonNullable<SseHandlers['onPauseRequested']>>[0]); break;
          case 'BuildCompleted':  handlers.onBuildCompleted?.(data as Parameters<NonNullable<SseHandlers['onBuildCompleted']>>[0]); break;
          case 'PushCompleted':   handlers.onPushCompleted?.(data as Parameters<NonNullable<SseHandlers['onPushCompleted']>>[0]); break;
          case 'DeployCompleted': handlers.onDeployCompleted?.(data as Parameters<NonNullable<SseHandlers['onDeployCompleted']>>[0]); break;
        }
      } catch { /* malformed event — ignore */ }
    };

    // EventSource reconnects automatically; onerror fires on each retry attempt
    this.es.onerror = () => {
      if (this.es?.readyState === EventSource.CONNECTING)
        handlers.onConnectionChange?.('reconnecting');
      else if (this.es?.readyState === EventSource.CLOSED)
        handlers.onConnectionChange?.('disconnected');
    };
  }

  /** Call after reconnect to replay accumulated log from the server record */
  async catchUp(buildId: string): Promise<IBuildRecord | null> {
    try {
      return await api.get<IBuildRecord>(`/api/builds/${buildId}`);
    } catch {
      return null;
    }
  }

  disconnect(): void {
    this.es?.close();
    this.es = null;
    this.activeBuildId = null;
    this.handlers = {};
  }
}

export const buildSse = new BuildSseService();
