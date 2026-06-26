import toast from 'react-hot-toast';
import { INotificationProvider } from './INotificationProvider';
import { NotificationPayload, NotificationType } from './types';

const EMOJI: Record<string, string> = {
  build_success: '\u2705',
  build_failed: '\u274C',
  pause_required: '\u23F0',
  push_success: '\u{1F680}',
  push_failed: '\u274C',
  deploy_success: '\u{1F389}',
  deploy_failed: '\u274C',
  db_op_completed: '\u2705',
  db_op_failed: '\u274C',
};

function toastForType(type: NotificationType) {
  if (type === 'pause_required') return toast.loading;
  if (type.endsWith('_failed')) return toast.error;
  return toast.success;
}

export class InAppToastNotificationProvider implements INotificationProvider {
  readonly name = 'in-app-toast';
  readonly isAvailable = true;

  async show(payload: NotificationPayload): Promise<boolean> {
    const show = toastForType(payload.type);
    show(`${EMOJI[payload.type] ?? ''} ${payload.message}`);
    return true;
  }
}
