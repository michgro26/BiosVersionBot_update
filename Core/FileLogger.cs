using System;
using System.IO;
using System.Text;

namespace BiosVersionBot.Core
{
    public sealed class FileLogger
    {
        private readonly string _logDirectory;
        private readonly object _lock = new();

        public FileLogger(string logDirectory)
        {
            _logDirectory = logDirectory;
            Directory.CreateDirectory(_logDirectory);
        }

        private string CurrentLogPath => Path.Combine(_logDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");

        public void Info(string message) => Write("INFO", message);
        public void Error(string message) => Write("ERROR", message);
        public void Critical(string message) => Write("CRITICAL", message);

        private void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
        }
    }
}
