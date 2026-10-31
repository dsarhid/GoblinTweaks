using System.Text;

namespace GoblinTweaks.Core;

/// <summary>
/// Writes to a file of its own any .NET exception about to close the game, whoever threw it, with what
/// GoblinTweaks did just before.
/// </summary>
/// <remarks>
/// When an exception escapes code the game calls directly, the process ends at once and Dalamud's crash
/// window only says "CLR error occurred", with no managed stack. Dalamud's own log also stops writing once it
/// reaches its size limit, which plugin reloads can fill. This file is written before the process dies, so
/// the exception can be read afterwards. Every plugin shares the process, so another plugin's exception is
/// caught too. The file lives next to the plugin's config: pluginConfigs/GoblinTweaks-crash.txt.
/// </remarks>
internal static class CrashRecorder
{
    private const int MaxNotes = 40;

    private static readonly Queue<string> Notes = new();
    private static readonly object NotesLock = new();
    private static string? _path;

    public static string? FilePath => _path;

    public static void Start()
    {
        _path = Path.Combine(Svc.PluginInterface.ConfigFile.DirectoryName ?? Svc.PluginInterface.GetPluginConfigDirectory(), "GoblinTweaks-crash.txt");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Note("GoblinTweaks loaded");
    }

    /// <summary>Must run on unload: a handler left behind keeps the unloaded plugin alive.</summary>
    public static void Stop()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    /// <summary>Remembers something GoblinTweaks just did; the last ones go into the file with the exception.</summary>
    public static void Note(string what)
    {
        lock (NotesLock)
        {
            Notes.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {what}");
            while (Notes.Count > MaxNotes) Notes.Dequeue();
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        // The process is going down: nothing here may throw.
        try
        {
            var text = new StringBuilder()
                .AppendLine("==================================================")
                .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} unhandled exception, terminating: {args.IsTerminating}")
                .AppendLine($"Thread: {Environment.CurrentManagedThreadId} {Thread.CurrentThread.Name}")
                .AppendLine()
                .AppendLine(args.ExceptionObject?.ToString() ?? "(no exception object)")
                .AppendLine()
                .AppendLine("Last things GoblinTweaks did:");

            lock (NotesLock)
            {
                foreach (var note in Notes)
                    text.AppendLine("  " + note);
            }

            text.AppendLine();
            if (_path != null) File.AppendAllText(_path, text.ToString());
        }
        catch
        {
            // Nothing left to do.
        }
    }
}
