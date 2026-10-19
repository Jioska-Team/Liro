using System.Text.Json;
using Liro.Application.Alerts;
namespace Liro.Infrastructure.Alerts;

public sealed class JsonBandwidthAlertStateStore(string directory, ulong channelId) : IBandwidthAlertStateStore
{
    private string FilePath => Path.Combine(directory, $"bandwidth-alerts-{channelId}.json");
    public async Task<BandwidthAlertStore.SavedState> LoadAsync(CancellationToken ct) => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<BandwidthAlertStore.SavedState>(await File.ReadAllTextAsync(FilePath, ct))
          ?? throw new InvalidDataException("Invalid alert state.")
        : new();
    public async Task SaveAsync(BandwidthAlertStore.SavedState state, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath + ".tmp", JsonSerializer.Serialize(state), ct);
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
