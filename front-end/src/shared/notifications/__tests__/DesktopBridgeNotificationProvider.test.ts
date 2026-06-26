import { DesktopBridgeNotificationProvider } from '../DesktopBridgeNotificationProvider';

describe('DesktopBridgeNotificationProvider', () => {
  let provider: DesktopBridgeNotificationProvider;
  let postMessageMock: jest.Mock;
  let origChrome: any;

  function setWebView2(hasWebView: boolean) {
    if (hasWebView) {
      postMessageMock = jest.fn();
      (globalThis as any).chrome = { webview: { postMessage: postMessageMock } };
    } else {
      (globalThis as any).chrome = undefined;
    }
  }

  beforeEach(() => {
    jest.clearAllMocks();
    origChrome = (globalThis as any).chrome;
    provider = new DesktopBridgeNotificationProvider();
  });

  afterEach(() => {
    (globalThis as any).chrome = origChrome;
  });

  describe('name', () => {
    it('returns provider name', () => {
      expect(provider.name).toBe('desktop-bridge');
    });
  });

  describe('isAvailable', () => {
    it('returns false when chrome.webview is undefined', () => {
      setWebView2(false);
      expect(provider.isAvailable).toBe(false);
    });

    it('returns true when chrome.webview.postMessage exists', () => {
      setWebView2(true);
      expect(provider.isAvailable).toBe(true);
    });
  });

  describe('show', () => {
    it('returns false when not in WebView2', async () => {
      setWebView2(false);
      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'done',
      });
      expect(result).toBe(false);
    });

    it('posts a message to the WebView2 host', async () => {
      setWebView2(true);

      const result = await provider.show({
        type: 'build_success',
        title: 'Build Complete',
        message: 'v2.2.0 built successfully',
        tag: 'build-42',
      });

      expect(result).toBe(true);
      expect(postMessageMock).toHaveBeenCalledTimes(1);
      const msg = postMessageMock.mock.calls[0][0];
      expect(msg.type).toBe('notification');
      expect(msg.notificationType).toBe('build_success');
      expect(msg.title).toBe('Build Complete');
      expect(msg.message).toBe('v2.2.0 built successfully');
    });

    it('includes tag in the bridge message when provided', async () => {
      setWebView2(true);

      await provider.show({
        type: 'build_failed',
        title: 'Build Failed',
        message: 'failed',
        tag: 'build-42',
      });

      const msg = postMessageMock.mock.calls[0][0];
      expect(msg.tag).toBe('build-42');
    });

    it('sends pause_required notification type', async () => {
      setWebView2(true);

      await provider.show({
        type: 'pause_required',
        title: 'Action Required',
        message: 'Input needed',
      });

      const msg = postMessageMock.mock.calls[0][0];
      expect(msg.notificationType).toBe('pause_required');
    });
  });
});
