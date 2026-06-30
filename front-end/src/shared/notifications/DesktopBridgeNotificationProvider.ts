import { INotificationProvider } from './INotificationProvider';
import { NotificationPayload } from './types';

interface DesktopNotificationMessage {
  type: 'notification';
  notificationType: string;
  title: string;
  message: string;
  tag?: string;
}

function getWebView2Host(): { postMessage(msg: unknown): void } | undefined {
  if (typeof window === 'undefined') return undefined;
  const w = window as any;
  return w.chrome?.webview?.postMessage ? w.chrome.webview : undefined;
}

export class DesktopBridgeNotificationProvider implements INotificationProvider {
  readonly name = 'desktop-bridge';

  get isAvailable(): boolean {
    return getWebView2Host() !== undefined;
  }

  async show(payload: NotificationPayload): Promise<boolean> {
    const host = getWebView2Host();
    if (!host) return false;

    const msg: DesktopNotificationMessage = {
      type: 'notification',
      notificationType: payload.type,
      title: payload.title,
      message: payload.message,
      tag: payload.tag,
    };

    host.postMessage(msg);
    return true;
  }
}
