using System.Globalization;
using RingView.Core.Diagnostics;

namespace RingView.Platform;

/// <summary>
/// The log file: <c>logs\ringview.log</c>, one line per entry, already redacted by
/// <see cref="DiagLog"/>. Rolls to <c>ringview.1.log</c> at 5 MB so it never grows without bound.
/// </summary>
public sealed class FileDiagSink : IDiagSink, IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    public FileDiagSink(string logsFolder)
    {
        Directory.CreateDirectory(logsFolder);
        _path = Path.Combine(logsFolder, "ringview.log");
    }

    public string FilePath => _path;

    public void Write(DiagLevel level, string scope, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level.ToString().ToUpperInvariant()}] {scope}: {message}");
        lock (_gate)
        {
            try
            {
                _writer ??= Open();
                if (_writer.BaseStream.Length > MaxBytes)
                {
                    _writer.Dispose();
                    File.Move(_path, Path.ChangeExtension(_path, ".1.log"), overwrite: true);
                    _writer = Open();
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Logging must never take the app down (disk full, file locked by a viewer).
            }
        }
    }

    private StreamWriter Open() =>
        new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
