using System.Collections.Concurrent;

namespace AdminConsole.Infrastructure.Telegram;

/// <summary>
/// Maps arbitrarily long strings (server names, group names, etc.) to short
/// numeric IDs for use in inline-button callback_data.
///
/// IMPORTANT: Telegram limits callback_data to 1-64 bytes. Embedding a long
/// string directly risks exceeding the limit and throwing an exception when
/// sending the keyboard.
///
/// Lives purely in process memory — not persistent. That's fine: the mapping
/// is only valid within the current "session" of the server list
/// (process restart — new button list, new IDs).
///
/// Carried over unchanged (T5.3).
/// </summary>
public sealed class TelegramCallbackRegistry
{
    private readonly ConcurrentDictionary<int, string> _forward = new();

    /// <summary>
    /// Reverse index for deduplication — without it, every call to Register()
    /// with the same value would create a new entry, and over months of 24/7
    /// operation _forward would grow without bound (memory leak).
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _reverse = new();

    private int _nextId;

    public int Register(string value)
    {
        if (_reverse.TryGetValue(value, out int existingId))
            return existingId;

        int candidateId = Interlocked.Increment(ref _nextId);

        int wonId = _reverse.GetOrAdd(value, candidateId);
        if (wonId == candidateId)
            _forward[candidateId] = value;

        return wonId;
    }

    public string? Resolve(int id) => _forward.TryGetValue(id, out var value) ? value : null;
}
