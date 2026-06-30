import { NotificationPayload } from './types';

export interface INotificationProvider {
  readonly name: string;
  readonly isAvailable: boolean;
  show(payload: NotificationPayload): Promise<boolean>;
}
