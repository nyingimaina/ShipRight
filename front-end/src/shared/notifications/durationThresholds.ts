import { NotificationType } from './types';

export const DEFAULT_DURATION_THRESHOLDS: Record<NotificationType, number> = {
  build_success: 60_000,
  build_failed: 0,
  pause_required: 0,
  push_success: 30_000,
  push_failed: 0,
  deploy_success: 120_000,
  deploy_failed: 0,
  db_op_completed: 30_000,
  db_op_failed: 0,
};
