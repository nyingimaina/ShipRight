import { INotificationProvider } from './INotificationProvider';
import { NotificationPayload, NotificationType } from './types';
import { DEFAULT_DURATION_THRESHOLDS } from './durationThresholds';

export class NotificationService {
  private provider: INotificationProvider;
  private thresholds: Record<NotificationType, number>;

  constructor(
    provider: INotificationProvider,
    thresholds?: Partial<Record<NotificationType, number>>
  ) {
    this.provider = provider;
    this.thresholds = { ...DEFAULT_DURATION_THRESHOLDS, ...thresholds };
  }

  async show(payload: NotificationPayload): Promise<boolean> {
    return this.provider.show(payload);
  }

  shouldNotify(type: NotificationType, elapsedMs: number): boolean {
    return elapsedMs >= this.thresholds[type];
  }
}
