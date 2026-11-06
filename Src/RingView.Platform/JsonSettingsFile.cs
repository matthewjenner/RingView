using System.Text;
using RingView.Core.Ring;

namespace RingView.Platform;

/// <summary>settings.json (no secrets), replaced atomically.</summary>
public sealed class JsonSettingsFile(string path) : ISettingsFile
{
    public async Task<string?> ReadAsync() =>
        File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : null;

    public Task WriteAsync(string json) => AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes(json));
}
