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

    // Enough to see a crash coming; a loop of caught exceptions must not fill the disk.
    private const int MaxFirstChance = 50;
    private static int _firstChanceWritten;

    [ThreadStatic]
    private static bool _inFirstChance;

    public static void Start()
    {
        _path = Path.Combine(Svc.PluginInterface.ConfigFile.DirectoryName ?? Svc.PluginInterface.GetPluginConfigDirectory(), "GoblinTweaks-crash.txt");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        Note("GoblinTweaks loaded");
    }

    /// <summary>Must run on unload: a handler left behind keeps the unloaded plugin alive.</summary>
    public static void Stop()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
    }

    /// <summary>
    /// Writes an exception the moment it is thrown, with the whole stack, when it can be what ends the game: any
    /// memory access violation, and any exception thrown by GoblinTweaks or KamiToolKit.
    /// </summary>
    /// <remarks>
    /// An exception thrown inside a callback of the game (a window event, an update) travels up through the game's
    /// own code to Dalamud's frame loop and ends the process there ("CLR error occurred"), without
    /// <see cref="AppDomain.UnhandledException"/> and with the stack already gone. Dalamud's log loses its last
    /// seconds too, as it writes in the background. Written here at once, it stays.
    /// </remarks>
    private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
    {
        if (_inFirstChance || _path == null || Volatile.Read(ref _firstChanceWritten) >= MaxFirstChance) return;

        var exception = args.Exception;
        var thrower   = exception.TargetSite?.DeclaringType?.Assembly;
        var relevant  = exception is AccessViolationException
                        || thrower == typeof(CrashRecorder).Assembly
                        || thrower?.GetName().Name == "KamiToolKit";
        if (!relevant) return;

        _inFirstChance = true;
        try
        {
            Interlocked.Increment(ref _firstChanceWritten);
            var text = new StringBuilder()
                .AppendLine("--------------------------------------------------")
                .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} thrown (may still be caught): {exception.GetType().FullName}: {exception.Message}")
                .AppendLine($"Thread: {Environment.CurrentManagedThreadId} {Thread.CurrentThread.Name}")
                .AppendLine(Environment.StackTrace)
                .AppendLine();
            File.AppendAllText(_path, text.ToString());
        }
        catch
        {
            // Never make things worse.
        }
        finally
        {
            _inFirstChance = false;
        }
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
