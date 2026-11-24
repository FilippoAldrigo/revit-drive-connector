using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitDriveConnector.Core.Utils
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Thread-safe rotating daily file logger stored in %APPDATA%\RevitDriveConnector\logs.
    /// Safe to call from Revit UI thread, background workers, and unit tests.
    /// </summary>
    public static class AppLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly string DefaultLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitDriveConnector",
            "logs");

        private static string _logDir = DefaultLogDir;
        private static DateTime _lastCleanupUtc = DateTime.MinValue;
        private const int RetentionDays = 7;
        private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

        public static string LogDirectory
        {
            get => _logDir;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _logDir = value;
                }
            }
        }

        public static void Debug(string component, string message) => Write(LogLevel.Debug, component, message, null);
        public static void Info(string component, string message) => Write(LogLevel.Info, component, message, null);
        public static void Warn(string component, string message, Exception? ex = null) => Write(LogLevel.Warn, component, message, ex);
        public static void Error(string component, string message, Exception? ex = null) => Write(LogLevel.Error, component, message, ex);

        private static void Write(LogLevel level, string component, string message, Exception? ex)
        {
            try
            {
                var now = DateTime.UtcNow;
                string line = FormatLine(now, level, component, message, ex);

                lock (SyncRoot)
                {
                    Directory.CreateDirectory(_logDir);
                    string logFile = Path.Combine(_logDir, $"connector-{now:yyyyMMdd}.log");

                    if (File.Exists(logFile))
                    {
                        var fi = new FileInfo(logFile);
                        if (fi.Length > MaxFileSizeBytes)
                        {
                            string rolled = Path.Combine(_logDir, $"connector-{now:yyyyMMdd-HHmmss}.log");
                            try { File.Move(logFile, rolled, true); } catch { }
                        }
                    }

                    File.AppendAllText(logFile, line, Encoding.UTF8);

                    if ((now - _lastCleanupUtc).TotalHours >= 6)
                    {
                        _lastCleanupUtc = now;
                        CleanupOldLogs(now);
                    }
                }
            }
            catch
            {
                // Logging must never crash Revit or background operations.
            }
        }

        private static string FormatLine(DateTime utcNow, LogLevel level, string component, string message, Exception? ex)
        {
            var sb = new StringBuilder(256);
            sb.Append(utcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append("Z [")
              .Append(level.ToString().ToUpperInvariant())
              .Append("] [")
              .Append(component)
              .Append("] ")
              .Append(message);

            if (ex != null)
            {
                sb.Append(" | EX: ")
                  .Append(ex.GetType().Name)
                  .Append(": ")
                  .Append(ex.Message);
                if (level == LogLevel.Error && !string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    sb.AppendLine().Append(ex.StackTrace);
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private static void CleanupOldLogs(DateTime utcNow)
        {
            try
            {
                if (!Directory.Exists(_logDir)) return;
                var cutoff = utcNow.AddDays(-RetentionDays);
                foreach (var file in Directory.GetFiles(_logDir, "connector-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
