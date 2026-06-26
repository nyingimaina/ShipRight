import { BrowserNotificationProvider } from './BrowserNotificationProvider';
import { CompositeNotificationProvider } from './CompositeNotificationProvider';
import { DesktopBridgeNotificationProvider } from './DesktopBridgeNotificationProvider';
import { InAppToastNotificationProvider } from './InAppToastNotificationProvider';
import { NotificationService } from './NotificationService';

const defaultProviders = [
  new DesktopBridgeNotificationProvider(),
  new BrowserNotificationProvider(),
  new InAppToastNotificationProvider(),
];

const composite = new CompositeNotificationProvider(defaultProviders);

export const notificationService = new NotificationService(composite);
