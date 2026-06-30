using Avalonia.Controls;

namespace ShipRight.Desktop.Services;

public interface IWebViewNavigationController
{
    event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted;
    Uri? Source { set; }
}
