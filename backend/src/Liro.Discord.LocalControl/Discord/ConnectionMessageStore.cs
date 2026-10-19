using System.Text.Json;

namespace Liro.Discord.LocalControl.Discord;

public interface IConnectionMessageTransport
{
    Task<ulong> CreateAsync(string content, CancellationToken token);
    Task<bool> UpdateAsync(ulong messageId, string content, CancellationToken token);
    Task DeleteAsync(ulong messageId, CancellationToken token);
}

// Only Discord identifiers are persisted locally, never URLs or session tokens.
public sealed class ConnectionMessageStore(string directory, ulong channelId, ulong panelId, IConnectionMessageTransport transport)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SavedMessage? current;
    private bool loaded;
    private long version;
    public long Version => Interlocked.Read(ref version);
    private string FilePath => Path.Combine(directory, "connection-message.json");
    private sealed record SavedMessage(ulong ChannelId, ulong PanelId, ulong MessageId);

    public async Task ShowAsync(string content, CancellationToken token, long? expectedVersion = null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (expectedVersion is { } expected && expected != version) { return; }
            Load();
            if (current is null || !await transport.UpdateAsync(current.MessageId, content, token))
                current = new(channelId, panelId, await transport.CreateAsync(content, token));
            Save();
        }
        finally { gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            Interlocked.Increment(ref version);
            Load();
            if (current is null) { return; }
            await transport.DeleteAsync(current.MessageId, token);
            File.Delete(FilePath);
            current = null;
        }
        finally { gate.Release(); }
    }

    private void Load()
    {
        if (loaded) { return; }
        if (File.Exists(FilePath))
        {
            var saved = JsonSerializer.Deserialize<SavedMessage>(File.ReadAllText(FilePath));
            if (saved is not null && saved.ChannelId == channelId && saved.PanelId == panelId
                && saved.MessageId != 0 && saved.MessageId != panelId) current = saved;
        }
        loaded = true;
    }

    private void Save()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(current));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
