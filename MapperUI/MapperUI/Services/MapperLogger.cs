using System;
using System.Collections.Generic;
using System.Diagnostics;
namespace MapperUI.Services;

namespace MapperUI
{
    public enum LogStep
    {
        PARSE,
        VALIDATE,
        DIFF,
        REMAP,
        WRITE,
        TOUCH,
        INFO,
        WARN,
        ERROR
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; init; }
        public LogStep Step { get; init; }
        public string Action { get; init; } = string.Empty;
    }

    /// <summary>
    /// Static logger all code in the codebase writes to.
    /// Subscribe on MainForm.Load, unsubscribe on close.
    /// </summary>
    public static class MapperLogger
    {
        public static event Action<LogEntry>? OnEntry;

        public static void Log(LogStep step, string action)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Step = step,
                Action = action
            };
            Debug.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{step}] {action}");
            OnEntry?.Invoke(entry);
        }

        // Convenience shortcuts
        public static void Parse(string msg) => Log(LogStep.PARSE, msg);
        public static void Validate(string msg) => Log(LogStep.VALIDATE, msg);
        public static void Diff(string msg) => Log(LogStep.DIFF, msg);
        public static void Remap(string msg) => Log(LogStep.REMAP, msg);
        public static void Write(string msg) => Log(LogStep.WRITE, msg);
        public static void Touch(string msg) => Log(LogStep.TOUCH, msg);
        public static void Info(string msg) => Log(LogStep.INFO, msg);
        public static void Warn(string msg) => Log(LogStep.WARN, msg);
        public static void Error(string msg) => Log(LogStep.ERROR, msg);
    }
}
}