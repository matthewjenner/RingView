namespace RingView.Platform;

/// <summary>Writes a file so a crash mid-write can never leave it half written.</summary>
internal static class AtomicFile
{
    public static async Task WriteAsync(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, contents).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true); // a rename on one volume: readers see old or new
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
