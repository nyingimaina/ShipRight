import { BrowserNotificationProvider } from '../BrowserNotificationProvider';

describe('BrowserNotificationProvider', () => {
  let provider: BrowserNotificationProvider;
  let notificationCtor: jest.Mock;
  let origNotification: any;

  function setNotificationMock(permission: NotificationPermission, requestPermission?: jest.Mock) {
    notificationCtor = jest.fn();
    (notificationCtor as any).permission = permission;
    (notificationCtor as any).requestPermission =
      requestPermission ?? jest.fn().mockResolvedValue('granted');
    (globalThis as any).Notification = notificationCtor as any;
  }

  beforeEach(() => {
    jest.clearAllMocks();
    origNotification = (globalThis as any).Notification;
    provider = new BrowserNotificationProvider();
  });

  afterEach(() => {
    (globalThis as any).Notification = origNotification;
  });

  describe('name', () => {
    it('returns provider name', () => {
      expect(provider.name).toBe('browser-notification');
    });
  });

  describe('isAvailable', () => {
    it('returns false when Notification is undefined', () => {
      (globalThis as any).Notification = undefined;
      expect(provider.isAvailable).toBe(false);
    });

    it('returns false when permission is denied', () => {
      setNotificationMock('denied');
      expect(provider.isAvailable).toBe(false);
    });

    it('returns true when permission is granted', () => {
      setNotificationMock('granted');
      expect(provider.isAvailable).toBe(true);
    });

    it('returns true when permission is default', () => {
      setNotificationMock('default');
      expect(provider.isAvailable).toBe(true);
    });
  });

  describe('show', () => {
    it('returns false when Notification is undefined', async () => {
      (globalThis as any).Notification = undefined;
      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'done',
      });
      expect(result).toBe(false);
    });

    it('creates a Notification when permission is granted', async () => {
      setNotificationMock('granted');

      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'v2.2.0 built successfully',
        tag: 'build-42',
      });

      expect(result).toBe(true);
      expect(notificationCtor).toHaveBeenCalledWith('Build Complete', {
        body: 'v2.2.0 built successfully',
        tag: 'build-42',
      });
    });

    it('uses title and message when no tag is provided', async () => {
      setNotificationMock('granted');

      await provider.show({
        type: 'build_success',
        title: 'Done',
        message: 'finished',
      });

      expect(notificationCtor).toHaveBeenCalledWith('Done', {
        body: 'finished',
        tag: undefined,
      });
    });

    it('calls requestPermission when permission is default then creates Notification', async () => {
      const requestPermission = jest.fn().mockResolvedValue('granted');
      setNotificationMock('default', requestPermission);

      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'done',
      });

      expect(result).toBe(true);
      expect(requestPermission).toHaveBeenCalledTimes(1);
      expect(notificationCtor).toHaveBeenCalled();
    });

    it('returns false when requestPermission returns denied', async () => {
      const requestPermission = jest.fn().mockResolvedValue('denied');
      setNotificationMock('default', requestPermission);

      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'done',
      });

      expect(result).toBe(false);
      expect(requestPermission).toHaveBeenCalled();
      expect(notificationCtor).not.toHaveBeenCalled();
    });

    it('returns false when permission is denied without requesting', async () => {
      setNotificationMock('denied');

      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'done',
      });

      expect(result).toBe(false);
      expect(notificationCtor).not.toHaveBeenCalled();
    });
  });
});
