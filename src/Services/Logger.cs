using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Thread-safe, daily-rolling logger.
/// Minimum level is controlled by AppLogLevel (Debug/Info/Warning).
/// Error is ALWAYS written regardless of level.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly string _logDirectory;
    private AppLogLevel     _minLevel;
    // Queue gives O(1) Enqueue/Dequeue — previously List.RemoveAt(0) was O(n)
    private readonly Queue<LogEntry> _history = new();
    private readonly object _lock = new();
    private StreamWriter?   _writer;
    private string          _currentLogPath = "";
    // when true, Warning/Error lines append the full ex.StackTrace
    // `volatile` ensures lock-free reads on hot paths
    // (TraceMode getter) eventually observe SetTraceMode writes.  Without
    // this, the JIT could hoist the field into a register on a long-running
    // loop and never reload it.
    private volatile bool   _traceMode;

    public event Action<LogEntry>? EntryAdded;

    public Logger(string logDirectory, AppLogLevel minLevel = AppLogLevel.Debug)
    {
        _logDirectory = logDirectory;
        _minLevel      = minLevel;
        Directory.CreateDirectory(_logDirectory);
        OpenLogFile();
        WriteRaw($"=== Logger started  level={minLevel}  dir={logDirectory} ===", flushImmediately: true);
    }

    public void SetLevel(AppLogLevel level)
    {
        // only log when the level actually changes — previous version
        // wrote "Уровень лога изменён" on every settings save even if the
        // user didn't touch the level dropdown (log noise on
        // repeated settings saves).
        bool changed;
        lock (_lock)
        {
            changed = _minLevel != level;
            _minLevel = level;
        }
        if (changed) Info($"Уровень лога изменён: {level}");
    }

    /// <summary>Enable/disable full stack-trace dumps for Warning/Error.</summary>
    public void SetTraceMode(bool enabled)
    {
        bool changed;
        lock (_lock)
        {
            changed = _traceMode != enabled;
            _traceMode = enabled;
        }
        if (changed) Info($"Режим трассировки: {(enabled ? "включён" : "выключен")}");
    }

    /// <summary>Current trace-mode flag. — used by hot paths to
    /// gate verbose diagnostic logging without paying the lock cost on
    /// every check.  The lock-free read is acceptable: a stale value lasts
    /// at most one operation.</summary>
    public bool TraceMode => _traceMode;

    public void Debug(string msg)           => Write(LogLevel.Debug,   msg, null);
    public void Info(string msg)            => Write(LogLevel.Info,    msg, null);
    public void Warn(string msg)            => Write(LogLevel.Warning, msg, null);
    /// <summary>Warning with an associated exception — stack trace shown in trace mode.</summary>
    public void Warn(string msg, Exception ex) => Write(LogLevel.Warning, msg, ex);
    public void Error(string msg, Exception? ex = null) => Write(LogLevel.Error, msg, ex);

    public IReadOnlyList<LogEntry> GetHistory()
    {
        lock (_lock) return _history.ToList();
    }

    /// <summary>
    /// Forces a flush of the underlying StreamWriter so on-disk content
    /// matches the in-memory buffer.  Called by the log-mirror feature
    /// before copying the active log file to the destination — without
    /// it, Debug/Info entries written since the last Warning/Error may
    /// still be buffered and would not appear in the mirrored copy.
    ///+.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { }
        }
    }

    /// <summary>Returns the directory the log files are written to.  Used by
    /// the log-mirror feature to enumerate today's and recent log files.
    ///+.</summary>
    public string LogDirectory => _logDirectory;

    // ── Internal ──────────────────────────────────────────────────────────────

    private void Write(LogLevel level, string msg, Exception? ex)
    {
        // Single lock for filter + allocate + write.
        // Previously this was two locks with a `new LogEntry(...)` allocation
        // between them: harmless functionally, but on Debug-level under high
        // event rates, the allocated entry could be thrown away if the level
        // had just changed.  One lock also makes the level snapshot consistent
        // with the write decision.
        LogEntry? entry = null;
        lock (_lock)
        {
            // Filter by minimum level — Errors always pass through
            if (level != LogLevel.Error)
            {
                AppLogLevel required = level switch
                {
                    LogLevel.Debug   => AppLogLevel.Debug,
                    LogLevel.Info    => AppLogLevel.Info,
                    LogLevel.Warning => AppLogLevel.Warning,
                    _                => AppLogLevel.Debug
                };
                if (required < _minLevel) return;
            }

            entry = new LogEntry(DateTime.Now, level, msg, ex);

            // Daily log roll
            string todayPath = Path.Combine(_logDirectory, $"pms-{DateTime.Today:yyyy-MM-dd}.log");
            if (todayPath != _currentLogPath)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _currentLogPath = todayPath;
                OpenLogFile();
            }

            string line = $"{entry.Timestamp:HH:mm:ss.fff} [{level,-7}] {msg}";
            if (ex is not null) line += $"  ‣ {ex.GetType().Name}: {ex.Message}";

            // Flush immediately for Warning/Error so crashes don't lose them.
            // Trace-mode stack dumps follow the main line — also flushed because the
            // main line is.  Debug/Info entries stay buffered until the next
            // important entry or log roll.
            bool important = level == LogLevel.Warning || level == LogLevel.Error;
            WriteRaw(line, flushImmediately: important);

            // Trace mode: append full stack trace for any Warning/Error with an exception.
            // Indented for readability; one frame per line preserved as-is.
            if (_traceMode && ex is not null && (level == LogLevel.Warning || level == LogLevel.Error))
            {
                string? trace = ex.StackTrace;
                if (!string.IsNullOrEmpty(trace))
                {
                    foreach (string frame in trace.Split('\n'))
                        WriteRaw($"    {frame.TrimEnd('\r')}", flushImmediately: false);
                }
                // Walk inner exceptions if any — these often carry the real cause
                Exception? inner = ex.InnerException;
                while (inner is not null)
                {
                    WriteRaw($"    --- inner: {inner.GetType().Name}: {inner.Message}", flushImmediately: false);
                    if (!string.IsNullOrEmpty(inner.StackTrace))
                    {
                        foreach (string frame in inner.StackTrace.Split('\n'))
                            WriteRaw($"    {frame.TrimEnd('\r')}", flushImmediately: false);
                    }
                    inner = inner.InnerException;
                }
                // Trace block finished — flush all of it together
                try { _writer?.Flush(); } catch { }
            }

            _history.Enqueue(entry);
            if (_history.Count > 10_000) _history.Dequeue(); // O(1) vs old O(n) RemoveAt(0)
        }

        // Fire event OUTSIDE lock to avoid deadlocks (entry is captured above).
        try { EntryAdded?.Invoke(entry); } catch { }
    }

    private void WriteRaw(string line, bool flushImmediately = false)
    {
        // Must be called inside _lock
        try
        {
            _writer?.WriteLine(line);
            // Flush only on important entries (Warning/Error) or trace dumps.
            // Info/Debug stay in StreamWriter buffer — flushed on disposal, log roll,
            // or any subsequent important entry. Saves ~100x fsync syscalls on bursts.
            if (flushImmediately) _writer?.Flush();
        }
        catch { }
    }

    private void OpenLogFile()
    {
        string path = Path.Combine(_logDirectory, $"pms-{DateTime.Today:yyyy-MM-dd}.log");
        _currentLogPath = path;
        try
        {
            // UTF-8 *without* BOM — easier to grep / pipe / tail
            var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            _writer = new StreamWriter(path, append: true, utf8NoBom) { AutoFlush = false };
        }
        catch (Exception ex)
        {
            // Can't open log file — not fatal; just disable file output
            _writer = null;
            Console.Error.WriteLine($"[Logger] Cannot open log file '{path}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Flush(); _writer?.Dispose(); _writer = null; }
    }
}
