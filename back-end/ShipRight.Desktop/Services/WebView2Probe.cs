using Avalonia.Controls;

namespace ShipRight.Desktop.Services;

public class WebView2Probe : IWebView2Probe
{
    private readonly IWebViewNavigationController _webView;

    public WebView2Probe(NativeWebView webView) : this(new WebViewNavigationAdapter(webView)) { }

    public WebView2Probe(IWebViewNavigationController webView)
    {
        _webView = webView;
    }

    public async Task<bool> ProbeAsync(TimeSpan timeout, Uri targetUrl)
    {
        var tcs = new TaskCompletionSource<bool>();

        EventHandler<WebViewNavigationCompletedEventArgs> handler = (_, args) =>
            tcs.TrySetResult(args.IsSuccess);

        _webView.NavigationCompleted += handler;
        _webView.Source = targetUrl;

        try
        {
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _webView.NavigationCompleted -= handler;
        }
    }
}
