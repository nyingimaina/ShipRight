using System.Collections.Concurrent;
using System.Threading.Channels;
using Newtonsoft.Json;

namespace ShipRight.Shared.Events;

public class BuildEventBus
{
    private readonly ConcurrentDictionary<string, List<Channel<string>>> _channels = new();
    private readonly object _lock = new();
    private static readonly JsonSerializerSettings _json = new()
    {
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
    };

    public ChannelReader<string> Subscribe(string buildId)
    {
        var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        lock (_lock)
            _channels.GetOrAdd(buildId, _ => new()).Add(ch);
        return ch.Reader;
    }

    public void Unsubscribe(string buildId, ChannelReader<string> reader)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(buildId, out var list))
                list.RemoveAll(ch => ch.Reader == reader);
        }
    }

    public Task EmitAsync(string buildId, string eventType, object data)
    {
        var payload = JsonConvert.SerializeObject(new { type = eventType, data }, _json);
        lock (_lock)
        {
            if (_channels.TryGetValue(buildId, out var list))
                foreach (var ch in list)
                    ch.Writer.TryWrite(payload);
        }
        return Task.CompletedTask;
    }

    public void Complete(string buildId)
    {
        lock (_lock)
        {
            if (_channels.TryRemove(buildId, out var list))
                foreach (var ch in list)
                    ch.Writer.TryComplete();
        }
    }
}
