using System;
using System.Collections.Generic;

namespace MapperUI.Services
{
    public enum LogStep
    {
        INFO,
        WARN,
        ERROR,
        PARSE,
        VALIDATE,
        DIFF,
        WRITE,
        TOUCH,
        REMAP
    }

    public record LogEntry(DateTime Timestamp, LogStep Step, string Action);

    /// <summary>
    /// Static logger that fires events AND buffers the last 500 entries.
    /// DebugConsoleForm subscribes to OnEntry for live updates and replays
    /// RecentEntries on construction to catch anything logged before it opened.
    /// </summary>
    public static class MapperLogger
    {
        // ── Event ────────────────────────────────────────────────────────────
        public static event Action<LogEntry>? OnEntry;

        // ── Buffer (ring, last 500 entries) ──────────────────────────────────
        private static readonly List<LogEntry> _buffer = new();
        private static readonly object _lock = new();
        private const int MaxBuffer = 500;

        public static IReadOnlyList<LogEntry> RecentEntries
        {
            get { lock (_lock) return _buffer.ToArray(); }
        }

        // ── Internal fire + buffer ────────────────────────────────────────────
        private static void Fire(LogStep step, string action)
        {
            var entry = new LogEntry(DateTime.Now, step, action);
            lock (_lock)
            {
                _buffer.Add(entry);
                if (_buffer.Count > MaxBuffer)
                    _buffer.RemoveAt(0);
            }
            OnEntry?.Invoke(entry);
        }

        // ── Public logging methods ────────────────────────────────────────────
        public static void Parse(string action) => Fire(LogStep.PARSE, action);
        public static void Validate(string action) => Fire(LogStep.VALIDATE, action);
        public static void Info(string action) => Fire(LogStep.INFO, action);
        public static void Warn(string action) => Fire(LogStep.WARN, action);
        public static void Error(string action) => Fire(LogStep.ERROR, action);
        public static void Diff(string action) => Fire(LogStep.DIFF, action);
        public static void Write(string action) => Fire(LogStep.WRITE, action);
        public static void Touch(string action) => Fire(LogStep.TOUCH, action);
        public static void Remap(string action) => Fire(LogStep.REMAP, action);
    }
}