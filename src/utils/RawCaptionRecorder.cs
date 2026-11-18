using System.IO;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranscriber.utils
{
    public static class RawCaptionRecorder
    {
        private static readonly object SyncRoot = new();
        private static StreamWriter? writer;
        private static string? lastText;

        public static string? LogPath { get; private set; }

        static RawCaptionRecorder()
        {
            if (!File.Exists(AppPaths.DiagnosticFlagFile))
                return;

            try
            {
                Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
                LogPath = Path.Combine(
                    AppPaths.DiagnosticsDirectory,
                    $"raw-captions-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
                writer = new StreamWriter(LogPath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARNING] Raw caption logging disabled: {ex.Message}");
                writer = null;
                LogPath = null;
            }
        }

        public static void Record(string text)
        {
            lock (SyncRoot)
            {
                if (writer == null || string.Equals(lastText, text, StringComparison.Ordinal))
                    return;

                var snapshot = new RawCaptionSnapshot(
                    DateTimeOffset.UtcNow,
                    Environment.TickCount64,
                    text);
                try
                {
                    writer.WriteLine(JsonSerializer.Serialize(snapshot));
                    lastText = text;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARNING] Raw caption logging stopped: {ex.Message}");
                    writer.Dispose();
                    writer = null;
                }
            }
        }

        public static void Shutdown()
        {
            lock (SyncRoot)
            {
                writer?.Flush();
                writer?.Dispose();
                writer = null;
            }
        }

        private sealed record RawCaptionSnapshot(
            DateTimeOffset CapturedAtUtc,
            long MonotonicMilliseconds,
            string Text);
    }
}
