using Avalonia.Controls;

namespace ShipRight.Desktop.Services;

public class WebViewNavigationAdapter : IWebViewNavigationController
{
    private readonly NativeWebView _webView;

    public WebViewNavigationAdapter(NativeWebView webView)
    {
        _webView = webView;
    }

    public event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted
    {
        add => _webView.NavigationCompleted += value;
        remove => _webView.NavigationCompleted -= value;
    }

    public Uri? Source
    {
        set => _webView.Source = value;
    }
}
