using System;
using System.IO;

namespace SorterUnlock;

internal static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpaceEngineers2", "SorterUnlock", "SorterUnlock.log");

    private static readonly object Gate = new();
    private static bool _rolled;

    internal static string FilePath => LogPath;

    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (!_rolled)
                {
                    _rolled = true;
                    // keep one previous session for comparison
                    if (File.Exists(LogPath))
                        File.Copy(LogPath, LogPath + ".prev", overwrite: true);
                    File.WriteAllText(LogPath, string.Empty);
                }
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the game
        }
    }

    internal static void Error(string what, Exception ex) => Write($"ERROR {what}: {ex}");
}
