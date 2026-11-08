namespace SaveGameCloud.Core.Infrastructure;

/// <summary>Journal texte minimal dans %APPDATA%\SaveGameCloud\logs.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var file = Path.Combine(AppPaths.LogDir, $"savegamecloud-{DateTime.Now:yyyy-MM}.log");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Le journal ne doit jamais faire planter l'application.
        }
    }
}
