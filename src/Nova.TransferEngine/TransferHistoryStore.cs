using System.Text.Json;
using Nova.Core.Contracts;

namespace Nova.TransferEngine;

public sealed class TransferHistoryStore
{
    private const int MaximumEntries = 1000;
    private readonly string _path;
    private readonly object _gate = new();
    private List<TransferHistoryEntry> _entries;

    public TransferHistoryStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "transfer-history.json");
        try { _entries = File.Exists(_path) ? JsonSerializer.Deserialize<List<TransferHistoryEntry>>(File.ReadAllText(_path)) ?? [] : []; }
        catch (JsonException) { _entries = []; }
        catch (IOException) { _entries = []; }
    }

    public IReadOnlyList<TransferHistoryEntry> GetRecent(int count = 100)
    {
        lock (_gate) return _entries.OrderByDescending(x => x.CompletedAt).Take(Math.Clamp(count, 1, MaximumEntries)).ToArray();
    }

    public void Add(TransferHistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaximumEntries) _entries = _entries.OrderByDescending(x => x.CompletedAt).Take(MaximumEntries).ToList();
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
            File.Move(temporary, _path, true);
        }
    }
}
