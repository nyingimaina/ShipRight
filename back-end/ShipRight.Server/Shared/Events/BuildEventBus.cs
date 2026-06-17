using System.Collections.Concurrent;
using System.Threading.Channels;
using Newtonsoft.Json;

namespace ShipRight.Shared.Events;

public class BuildEventBus
{
    private sealed class OpState
    {
        public List<string> Buffer    { get; } = [];
        public List<Channel<string>> Subscribers { get; } = [];
        public bool Completed { get; set; }
    }

    private readonly ConcurrentDictionary<string, OpState> _ops = new();
    private readonly object _lock = new();
    private static readonly JsonSerializerSettings _json = new()
    {
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
    };

    // Call BEFORE starting the background operation.
    // Ensures events emitted before a subscriber connects are buffered for replay.
    public void Register(string opId)
    {
        lock (_lock)
            _ops.TryAdd(opId, new OpState());
    }

    public ChannelReader<string> Subscribe(string opId)
    {
        var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        lock (_lock)
        {
            var state = _ops.GetOrAdd(opId, _ => new OpState());
            // Replay buffered events so late subscribers don't miss anything
            foreach (var msg in state.Buffer)
                ch.Writer.TryWrite(msg);
            if (state.Completed)
                ch.Writer.TryComplete();
            else
                state.Subscribers.Add(ch);
        }
        return ch.Reader;
    }

    public void Unsubscribe(string opId, ChannelReader<string> reader)
    {
        lock (_lock)
        {
            if (_ops.TryGetValue(opId, out var state))
                state.Subscribers.RemoveAll(ch => ch.Reader == reader);
        }
    }

    public Task EmitAsync(string opId, string eventType, object data)
    {
        var payload = JsonConvert.SerializeObject(new { type = eventType, data }, _json);
        lock (_lock)
        {
            var state = _ops.GetOrAdd(opId, _ => new OpState());
            state.Buffer.Add(payload);
            foreach (var ch in state.Subscribers)
                ch.Writer.TryWrite(payload);
        }
        return Task.CompletedTask;
    }

    public void Complete(string opId)
    {
        lock (_lock)
        {
            if (_ops.TryGetValue(opId, out var state))
            {
                state.Completed = true;
                foreach (var ch in state.Subscribers)
                    ch.Writer.TryComplete();
                state.Subscribers.Clear();
            }
        }
        // Clean up buffer after 5 minutes to avoid unbounded growth
        _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(t => _ops.TryRemove(opId, out _));
    }
}
