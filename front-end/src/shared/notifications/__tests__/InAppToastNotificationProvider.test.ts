import toast from 'react-hot-toast';
import { InAppToastNotificationProvider } from '../InAppToastNotificationProvider';

jest.mock('react-hot-toast', () => ({
  __esModule: true,
  default: { success: jest.fn(), error: jest.fn(), loading: jest.fn() },
}));

describe('InAppToastNotificationProvider', () => {
  let provider: InAppToastNotificationProvider;

  beforeEach(() => {
    jest.clearAllMocks();
    provider = new InAppToastNotificationProvider();
  });

  describe('name', () => {
    it('returns provider name', () => {
      expect(provider.name).toBe('in-app-toast');
    });
  });

  describe('isAvailable', () => {
    it('is always true', () => {
      expect(provider.isAvailable).toBe(true);
    });
  });

  describe('show', () => {
    it('calls toast.success for build_success', async () => {
      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'v2.2.0 built successfully',
      });
      expect(result).toBe(true);
      expect(toast.success).toHaveBeenCalledWith(
        expect.stringContaining('v2.2.0 built successfully')
      );
      expect(toast.error).not.toHaveBeenCalled();
      expect(toast.loading).not.toHaveBeenCalled();
    });

    it('calls toast.error for build_failed', async () => {
      await provider.show({
        type: 'build_failed',
        title: 'Build Failed',
        message: 'v2.2.0 build failed',
      });
      expect(toast.error).toHaveBeenCalledWith(
        expect.stringContaining('v2.2.0 build failed')
      );
    });

    it('calls toast.error for push_failed', async () => {
      await provider.show({
        type: 'push_failed',
        title: 'Push Failed',
        message: 'Push to registry failed',
      });
      expect(toast.error).toHaveBeenCalled();
    });

    it('calls toast.loading for pause_required', async () => {
      await provider.show({
        type: 'pause_required',
        title: 'Action Required',
        message: 'Build needs your input',
      });
      expect(toast.loading).toHaveBeenCalledWith(
        expect.stringContaining('Build needs your input')
      );
    });

    it('calls toast.success for deploy_success', async () => {
      await provider.show({
        type: 'deploy_success',
        title: 'Deployed',
        message: 'Production deploy complete',
      });
      expect(toast.success).toHaveBeenCalled();
    });

    it('calls toast.error for deploy_failed', async () => {
      await provider.show({
        type: 'deploy_failed',
        title: 'Deploy Failed',
        message: 'Deployment failed',
      });
      expect(toast.error).toHaveBeenCalled();
    });

    it('calls toast.success for push_success', async () => {
      await provider.show({
        type: 'push_success',
        title: 'Push Complete',
        message: 'Pushed to registry',
      });
      expect(toast.success).toHaveBeenCalled();
    });

    it('includes an emoji prefix in the message', async () => {
      await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'v2.2.0 built',
      });
      const callArg = (toast.success as jest.Mock).mock.calls[0][0];
      expect(callArg).toMatch(/^. \s*v2\.2\.0 built/);
    });
  });
});
