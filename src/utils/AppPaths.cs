using System.IO;

namespace LiveCaptionsTranscriber.utils
{
    public static class AppPaths
    {
        public static readonly string ExecutableDirectory =
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        public static readonly string SettingsFile =
            Path.Combine(ExecutableDirectory, "setting.json");

        public static readonly string DatabaseFile =
            Path.Combine(ExecutableDirectory, "transcription_history.db");

        public static readonly string DiagnosticFlagFile =
            Path.Combine(ExecutableDirectory, "enable-raw-caption-log.flag");

        public static readonly string DiagnosticsDirectory =
            Path.Combine(ExecutableDirectory, "diagnostics");

        public static readonly string TranscriptsDirectory =
            Path.Combine(ExecutableDirectory, "transcripts");
    }
}
