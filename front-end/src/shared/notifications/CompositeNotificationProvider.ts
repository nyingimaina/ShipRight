import { INotificationProvider } from './INotificationProvider';
import { NotificationPayload } from './types';

export class CompositeNotificationProvider implements INotificationProvider {
  readonly name = 'composite';

  private readonly providers: readonly INotificationProvider[];

  constructor(providers: INotificationProvider[]) {
    if (providers.length === 0) {
      throw new Error('CompositeNotificationProvider requires at least one provider');
    }
    this.providers = providers;
  }

  get isAvailable(): boolean {
    return this.providers.some((p) => p.isAvailable);
  }

  async show(payload: NotificationPayload): Promise<boolean> {
    for (const provider of this.providers) {
      if (!provider.isAvailable) continue;
      if (await provider.show(payload)) {
        return true;
      }
    }
    return false;
  }
}
