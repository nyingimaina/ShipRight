import { NotificationService } from '../NotificationService';
import { INotificationProvider } from '../INotificationProvider';
import { NotificationPayload } from '../types';

describe('NotificationService', () => {
  let service: NotificationService;
  let mockProvider: jest.Mocked<INotificationProvider>;
  let showSpy: jest.Mock;

  beforeEach(() => {
    showSpy = jest.fn().mockResolvedValue(true);
    mockProvider = {
      name: 'mock',
      isAvailable: true,
      show: showSpy,
    };
    service = new NotificationService(mockProvider);
  });

  describe('show', () => {
    it('delegates to the provider', async () => {
      const payload: NotificationPayload = {
        type: 'build_success',
        title: 'Build Complete',
        message: 'done',
      };

      const result = await service.show(payload);

      expect(result).toBe(true);
      expect(showSpy).toHaveBeenCalledWith(payload);
    });

    it('returns false when provider returns false', async () => {
      showSpy.mockResolvedValue(false);

      const result = await service.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'done',
      });

      expect(result).toBe(false);
    });
  });

  describe('shouldNotify', () => {
    it('returns true when elapsed exceeds default threshold', () => {
      expect(service.shouldNotify('build_success', 120_000)).toBe(true);
    });

    it('returns false when elapsed is below default threshold', () => {
      expect(service.shouldNotify('build_success', 30_000)).toBe(false);
    });

    it('returns true at exactly the threshold boundary', () => {
      expect(service.shouldNotify('build_success', 60_000)).toBe(true);
    });

    it('always returns true for failure types (threshold = 0)', () => {
      expect(service.shouldNotify('build_failed', 0)).toBe(true);
      expect(service.shouldNotify('build_failed', 100)).toBe(true);
    });

    it('always returns true for pause_required (threshold = 0)', () => {
      expect(service.shouldNotify('pause_required', 0)).toBe(true);
    });
  });

  describe('constructor', () => {
    it('uses default thresholds when none provided', () => {
      expect(service.shouldNotify('deploy_success', 60_000)).toBe(false);
      expect(service.shouldNotify('deploy_success', 120_000)).toBe(true);
    });

    it('allows overriding thresholds', () => {
      const custom = new NotificationService(mockProvider, {
        build_success: 10_000,
      });

      expect(custom.shouldNotify('build_success', 5_000)).toBe(false);
      expect(custom.shouldNotify('build_success', 10_000)).toBe(true);
    });
  });
});
