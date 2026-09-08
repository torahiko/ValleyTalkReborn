using System;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    /// <summary>
    /// Cross-platform compatible logger for ValleyTalk
    /// Provides a static wrapper for SMAPI's IMonitor
    /// </summary>
    public static class Log
    {
        private static IMonitor _monitor;
        
        /// <summary>
        /// Initialize the logger with SMAPI's monitor
        /// </summary>
        /// <param name="monitor">The SMAPI monitor instance</param>
        public static void Initialize(IMonitor monitor)
        {
            _monitor = monitor;
        }

        /// <summary>
        /// Log a debug message
        /// </summary>
        public static void Debug(string message)
        {
            _monitor?.Log(message, LogLevel.Debug);
        }

        /// <summary>
        /// Log a debug message with string formatting safely
        /// </summary>
        public static void Debug(string format, params object[] args)
        {
            _monitor?.Log(SafeFormat(format, args), LogLevel.Debug);
        }

        /// <summary>
        /// Log an error message
        /// </summary>
        public static void Error(string message)
        {
            _monitor?.Log(message, LogLevel.Error);
        }

        /// <summary>
        /// Log an error with an exception
        /// </summary>
        public static void Error(Exception ex, string message)
        {
            var fullMessage = ex != null 
                ? $"{message}: {ex.Message}\n{ex.StackTrace}" 
                : message;
            _monitor?.Log(fullMessage, LogLevel.Error);
        }

        /// <summary>
        /// Log an informational message
        /// </summary>
        public static void Information(string message)
        {
            _monitor?.Log(message, LogLevel.Info);
        }

        /// <summary>
        /// Log an informational message with string formatting safely
        /// </summary>
        public static void Information(string format, params object[] args)
        {
            _monitor?.Log(SafeFormat(format, args), LogLevel.Info);
        }

        /// <summary>
        /// Log a warning message
        /// </summary>
        public static void Warning(string message)
        {
            _monitor?.Log(message, LogLevel.Warn);
        }

        /// <summary>
        /// Log a warning message with string formatting safely
        /// </summary>
        public static void Warning(string format, params object[] args)
        {
            _monitor?.Log(SafeFormat(format, args), LogLevel.Warn);
        }

        /// <summary>
        /// 安全格式化字符串，防止含 {} 的文本（如 JSON/Prompt）引发 FormatException 崩溃
        /// </summary>
        private static string SafeFormat(string format, object[] args)
        {
            if (string.IsNullOrEmpty(format)) return string.Empty;
            if (args == null || args.Length == 0) return format;

            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                // 如果格式化失败（例如字符串中包含未转义的花括号），直接返回原字符串与参数拼接
                return $"{format} [Args: {string.Join(", ", args)}]";
            }
        }

        /// <summary>
        /// Resets the monitor reference. Called when the game is exiting.
        /// </summary>
        public static void Cleanup()
        {
            _monitor = null;
        }

        #region Serilog Compatibility Stubs (可根据项目实际情况保留或删除)
        public static class Logger
        {
            public static IMonitor Monitor => _monitor;
            public static LoggerConfiguration CreateLogger() => new LoggerConfiguration();
        }
        
        public class LoggerConfiguration 
        {
            public LoggerConfiguration WriteTo => this;
            public LoggerConfiguration Console() => this;
            public LoggerConfiguration File(string path, object rollingInterval) => this;
            public LoggerConfiguration MinimumLevel => this;
            public LoggerConfiguration Debug() => this;
        }
        #endregion
    }
}