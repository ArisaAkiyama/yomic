using System;

namespace Yomic.Core.Services
{
    /// <summary>
    /// Centralized logging service with colored console output.
    /// Yellow = Warning, Red = Error, Cyan = Info, White = Debug
    /// </summary>
    public static class LogService
    {
        private static readonly object _lock = new();
        
        public static void Debug(string tag, string message)
        {
            WriteLog(ConsoleColor.Gray, "DEBUG", tag, message);
        }
        
        public static void Info(string tag, string message)
        {
            WriteLog(ConsoleColor.Cyan, "INFO", tag, message);
        }
        
        public static void Warning(string tag, string message)
        {
            WriteLog(ConsoleColor.Yellow, "WARN", tag, message);
        }
        
        public static void Error(string tag, string message)
        {
            WriteLog(ConsoleColor.Red, "ERROR", tag, message);
        }
        
        public static void Error(string tag, string message, Exception ex)
        {
            WriteLog(ConsoleColor.Red, "ERROR", tag, $"{message}: {ex.Message}");
            if (ex.StackTrace != null)
            {
                WriteLog(ConsoleColor.DarkRed, "TRACE", tag, ex.StackTrace);
            }
        }
        
        public static void Success(string tag, string message)
        {
            WriteLog(ConsoleColor.Green, "OK", tag, message);
        }
        
        private static void WriteLog(ConsoleColor color, string level, string tag, string message)
        {
            lock (_lock)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                var originalColor = Console.ForegroundColor;
                
                // Timestamp in gray
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{timestamp}] ");
                
                // Level with color
                Console.ForegroundColor = color;
                Console.Write($"[{level}] ");
                
                // Tag in white
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"[{tag}] ");
                
                // Message with level color
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                
                // Reset
                Console.ForegroundColor = originalColor;
                
                // Also write to Debug output for IDE visibility
                System.Diagnostics.Debug.WriteLine($"[{timestamp}] [{level}] [{tag}] {message}");
            }
        }
    }
}
