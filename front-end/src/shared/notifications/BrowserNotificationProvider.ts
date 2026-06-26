import { INotificationProvider } from './INotificationProvider';
import { NotificationPayload } from './types';

function getApi(): typeof Notification | undefined {
  if (typeof Notification === 'undefined') return undefined;
  return Notification;
}

export class BrowserNotificationProvider implements INotificationProvider {
  readonly name = 'browser-notification';

  get isAvailable(): boolean {
    const api = getApi();
    if (!api) return false;
    return api.permission !== 'denied';
  }

  async show(payload: NotificationPayload): Promise<boolean> {
    const api = getApi();
    if (!api) return false;

    if (api.permission === 'granted') {
      new api(payload.title, { body: payload.message, tag: payload.tag });
      return true;
    }

    if (api.permission === 'default') {
      const permission = await api.requestPermission();
      if (permission === 'granted') {
        new api(payload.title, { body: payload.message, tag: payload.tag });
        return true;
      }
    }

    return false;
  }
}
