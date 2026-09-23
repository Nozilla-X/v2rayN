using v2rayN.Web.Contracts;
using ServiceLib.Common;

namespace v2rayN.Web.Services;

public sealed class LogBuffer
{
    private const int Capacity = 2000;
    private readonly object _gate = new();
    private readonly Queue<LogView> _items = new();

    public LogView Add(string source, string message)
    {
        var entry = new LogView(DateTimeOffset.UtcNow, source, message.TrimEnd());
        lock (_gate)
        {
            _items.Enqueue(entry);
            while (_items.Count > Capacity)
            {
                _items.Dequeue();
            }
        }

        return entry;
    }

    public IReadOnlyList<LogView> Recent(int limit, string? filter = null)
    {
        lock (_gate)
        {
            var query = _items.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                query = query.Where(item => Utils.IsRegexMatch($"{item.Source} {item.Message}", filter));
            }
            return query.TakeLast(Math.Clamp(limit, 1, Capacity)).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}
