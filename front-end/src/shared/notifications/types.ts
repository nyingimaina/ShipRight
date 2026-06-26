export type NotificationType =
  | 'build_success'
  | 'build_failed'
  | 'pause_required'
  | 'push_success'
  | 'push_failed'
  | 'deploy_success'
  | 'deploy_failed'
  | 'db_op_completed'
  | 'db_op_failed';

export interface NotificationPayload {
  type: NotificationType;
  title: string;
  message: string;
  actionUrl?: string;
  tag?: string;
}
