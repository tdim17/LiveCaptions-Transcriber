using System.IO;
using System.Text;

namespace LiveCaptionsTranscriber.utils
{
    public static class TranscriptFileWriter
    {
        private static readonly object SyncRoot = new();
        private static readonly string SessionTimestamp =
            DateTime.Now.ToString("yyyyMMdd-HHmmss");
        private static string lastWrittenText = string.Empty;

        public static readonly string FilePath = Path.Combine(
            AppPaths.TranscriptsDirectory,
            $"transcript-{SessionTimestamp}.txt");

        public static void Write(string text)
        {
            if (string.Equals(text, lastWrittenText, StringComparison.Ordinal))
                return;

            lock (SyncRoot)
            {
                if (string.Equals(text, lastWrittenText, StringComparison.Ordinal))
                    return;

                try
                {
                    Directory.CreateDirectory(AppPaths.TranscriptsDirectory);
                    string temporaryPath = FilePath + ".tmp";
                    File.WriteAllText(temporaryPath, text, new UTF8Encoding(false));
                    File.Move(temporaryPath, FilePath, true);
                    lastWrittenText = text;
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[ERROR] Saving transcript failed: {exception.Message}");
                }
            }
        }

        public static void Clear()
        {
            Write(string.Empty);
        }
    }
}
