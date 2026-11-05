namespace ProxMonitor.Core;

/// <summary>
/// Diagnostic log of the work with real servers, for sending to whoever debugs the app.
/// Token secrets and SSH passwords are never passed in here.
/// </summary>
public static class Log
{
    const long MaxBytes = 2 << 20; // the previous run's log is kept as .old beyond this
    static readonly Lock Gate = new();

    /// <summary>Null (the default, e.g. in tests) writes nothing.</summary>
    public static string? FilePath { get; set; }

    public static void Start(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
        }
        catch (IOException)
        {
        }
        FilePath = path;
    }

    public static void Write(string who, string text)
    {
        if (FilePath is not { } path) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{who}] {text}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the log must never break monitoring
            }
        }
    }
}
