import { CompositeNotificationProvider } from '../CompositeNotificationProvider';
import { INotificationProvider } from '../INotificationProvider';
import { NotificationPayload } from '../types';

describe('CompositeNotificationProvider', () => {
  let provider: CompositeNotificationProvider;
  let mockA: jest.Mocked<INotificationProvider>;
  let mockB: jest.Mocked<INotificationProvider>;
  let mockC: jest.Mocked<INotificationProvider>;
  const payload: NotificationPayload = {
    type: 'build_success',
    title: 'Build Complete',
    message: 'done',
  };
  const setAvailability = (provider: jest.Mocked<INotificationProvider>, value: boolean) => {
    (provider as { isAvailable: boolean }).isAvailable = value;
  };

  beforeEach(() => {
    mockA = { name: 'a', isAvailable: false, show: jest.fn().mockResolvedValue(false) };
    mockB = { name: 'b', isAvailable: false, show: jest.fn().mockResolvedValue(false) };
    mockC = { name: 'c', isAvailable: true, show: jest.fn().mockResolvedValue(true) };

    provider = new CompositeNotificationProvider([mockA, mockB, mockC]);
  });

  describe('name', () => {
    it('returns composite name', () => {
      expect(provider.name).toBe('composite');
    });
  });

  describe('isAvailable', () => {
    it('returns true when at least one provider is available', () => {
      expect(provider.isAvailable).toBe(true);
    });

    it('returns false when no provider is available', () => {
      setAvailability(mockC, false);
      expect(provider.isAvailable).toBe(false);
    });
  });

  describe('show', () => {
    it('tries first available provider and stops', async () => {
      setAvailability(mockA, true);
      mockA.show.mockResolvedValue(true);

      const result = await provider.show(payload);

      expect(result).toBe(true);
      expect(mockA.show).toHaveBeenCalledWith(payload);
      expect(mockB.show).not.toHaveBeenCalled();
      expect(mockC.show).not.toHaveBeenCalled();
    });

    it('skips unavailable providers and falls through', async () => {
      setAvailability(mockA, false);
      setAvailability(mockB, true);
      mockB.show.mockResolvedValue(true);

      const result = await provider.show(payload);

      expect(result).toBe(true);
      expect(mockA.show).not.toHaveBeenCalled();
      expect(mockB.show).toHaveBeenCalledWith(payload);
      expect(mockC.show).not.toHaveBeenCalled();
    });

    it('skips unavailable providers entirely', async () => {
      setAvailability(mockA, false);
      setAvailability(mockB, false);
      setAvailability(mockC, true);

      await provider.show(payload);

      expect(mockA.show).not.toHaveBeenCalled();
      expect(mockB.show).not.toHaveBeenCalled();
      expect(mockC.show).toHaveBeenCalledTimes(1);
    });

    it('returns false when all providers show returns false even if available', async () => {
      setAvailability(mockA, true);
      mockA.show.mockResolvedValue(false);

      await provider.show(payload);

      expect(mockA.show).toHaveBeenCalled();
      expect(mockB.show).not.toHaveBeenCalled();
      expect(mockC.show).toHaveBeenCalled();
    });

    it('returns false when no provider handles the notification', async () => {
      setAvailability(mockA, true);
      mockA.show.mockResolvedValue(false);
      setAvailability(mockC, false);

      const result = await provider.show(payload);

      expect(result).toBe(false);
      expect(mockA.show).toHaveBeenCalled();
      expect(mockB.show).not.toHaveBeenCalled();
      expect(mockC.show).not.toHaveBeenCalled();
    });

    it('throws when providers array is empty', () => {
      expect(() => new CompositeNotificationProvider([])).toThrow('at least one provider');
    });
  });
});
