namespace ShipRight.Desktop.Services;

public interface IWebView2Probe
{
    Task<bool> ProbeAsync(TimeSpan timeout, Uri targetUrl);
}
