using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed class EventHub
{
    private const int EventBufferCapacity = 256;
    private readonly ConcurrentDictionary<Guid, Channel<WebEvent>> _subscribers = new();

    public void Publish(string type, object data)
    {
        var message = new WebEvent(type, data, DateTimeOffset.UtcNow);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(message);
        }
    }

    public async IAsyncEnumerable<WebEvent> Subscribe(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<WebEvent>(new BoundedChannelOptions(EventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[id] = channel;
        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}
