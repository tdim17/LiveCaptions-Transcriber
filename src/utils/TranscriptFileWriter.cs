using System.IO;
using System.Text;

namespace LiveCaptionsTranscriber.utils
{
    public static class TranscriptFileWriter
    {
        private static readonly object SyncRoot = new();
        private static string lastWrittenText = string.Empty;
        private static string? filePath;
        private static bool sessionActive;

        public static string? FilePath
        {
            get
            {
                lock (SyncRoot)
                    return filePath;
            }
        }

        public static string BeginSession()
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(AppPaths.TranscriptsDirectory);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string candidate = Path.Combine(
                    AppPaths.TranscriptsDirectory,
                    $"{timestamp}_transcript.txt");
                int suffix = 2;
                while (File.Exists(candidate))
                {
                    candidate = Path.Combine(
                        AppPaths.TranscriptsDirectory,
                        $"{timestamp}_transcript_{suffix}.txt");
                    suffix++;
                }

                filePath = candidate;
                lastWrittenText = string.Empty;
                sessionActive = true;
                File.WriteAllText(filePath, string.Empty, new UTF8Encoding(false));
                return filePath;
            }
        }

        public static void Write(string text)
        {
            lock (SyncRoot)
            {
                if (!sessionActive || filePath == null)
                    return;
                if (string.Equals(text, lastWrittenText, StringComparison.Ordinal))
                    return;

                try
                {
                    Directory.CreateDirectory(AppPaths.TranscriptsDirectory);
                    string temporaryPath = filePath + ".tmp";
                    File.WriteAllText(temporaryPath, text, new UTF8Encoding(false));
                    File.Move(temporaryPath, filePath, true);
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

        public static void EndSession()
        {
            lock (SyncRoot)
            {
                sessionActive = false;
                lastWrittenText = string.Empty;
            }
        }
    }
}
