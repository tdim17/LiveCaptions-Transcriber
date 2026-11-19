using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using System.Windows.Automation;

using LiveCaptionsTranscriber.models;
using LiveCaptionsTranscriber.utils;

namespace LiveCaptionsTranscriber
{
    public static class Transcriber
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly ConcurrentQueue<string> pendingTextQueue = new();
        private static readonly TranscriptAssembler transcriptAssembler = new();
        private static readonly object transcriptLock = new();
        private static int stopping;
        private static int shutdownStarted;


        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool TranscriptionOnlyMode { get; set; } = true;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranscriptionLogged;

        static Transcriber()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);

            if (!File.Exists(models.Setting.FILE_PATH))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                    RawCaptionRecorder.Record(fullText);
                }
                catch (ElementNotAvailableException)
                {
                    FinalizeCurrentSegment();
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                string transcriptSnapshot = fullText;

                // Preprocess
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                TranscriptUpdate transcriptUpdate;
                lock (transcriptLock)
                {
                    transcriptUpdate = transcriptAssembler.Apply(transcriptSnapshot);
                    Caption.FullTranscriptionText = transcriptUpdate.FullText;
                    TranscriptFileWriter.Write(transcriptUpdate.FullText);
                }

                if (!string.IsNullOrWhiteSpace(transcriptUpdate.NewlyCommittedText))
                    pendingTextQueue.Enqueue(transcriptUpdate.NewlyCommittedText);
                if (transcriptUpdate.SafetyViolation)
                    Console.WriteLine("[WARNING] Live Captions changed text outside the two-sentence safety window.");

                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.Contexts.Count > 0)
                {
                    Caption.Contexts.Clear();
                    Caption.OnPropertyChanged("DisplayContexts");
                }

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);

                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
                Caption.OverlayOriginalCaption = latestCaption;
                for (int historyCount = Math.Min(Setting.OverlayWindow.HistoryMax, Caption.Contexts.Count);
                     historyCount > 0 && lastEOSIndex > 0;
                     historyCount--)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    Caption.OverlayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                }
                // Caption.DisplayOriginalCaption =
                //     TextUtil.ShortenDisplaySentence(Caption.OverlayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);

                // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption =
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);
                // `OriginalCaption`: The sentence to be really translated.
                if (string.CompareOrdinal(Caption.OriginalCaption, latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;
                }

                Thread.Sleep(25);
            }
        }

        public static async Task TranscribeLoop()
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                // Check LiveCaptions.exe still alive
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                // Log transcription only
                if (pendingTextQueue.TryDequeue(out string? originalSnapshot))
                {
                    await LogOnly(originalSnapshot);
                }

                await Task.Delay(40);
            }
        }

        public static async Task DisplayLoop()
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                // Clear translation display since we're transcription-only
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayTranslatedCaption = "Transcription Only Mode";
                Caption.OverlayTranslatedCaption = string.Empty;

                Thread.Sleep(100);
            }
        }



        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranscription(token);
                await SQLiteHistoryLogger.LogTranscription(originalText, translatedText, "N/A", "Transcription");
                TranscriptionLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Logging History Failed: {ex.Message}");
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranscription(token);
                await SQLiteHistoryLogger.LogTranscription(originalText, "Transcription Only", "N/A", "Transcription");
                TranscriptionLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Logging History Failed: {ex.Message}");
            }
        }

        public static async Task AddLogCard(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranscription(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Setting?.MainWindow.CaptionLogMax)
                Caption.Contexts.Dequeue();
            Caption?.Contexts.Enqueue(lastLog);
            Caption?.OnPropertyChanged("DisplayContexts");
        }

        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            // If this text is too similar to the last one, rewrite it when logging.
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;
            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > 0.66;
        }

        public static void ClearTranscription()
        {
            lock (transcriptLock)
            {
                transcriptAssembler.Reset();
                while (pendingTextQueue.TryDequeue(out _))
                {
                }
                Caption?.ClearFullTranscription();
                TranscriptFileWriter.Clear();
            }
        }

        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
                return;

            Volatile.Write(ref stopping, 1);
            FinalizeCurrentSegment();

            while (pendingTextQueue.TryDequeue(out string? text))
            {
                if (!string.IsNullOrWhiteSpace(text))
                    LogOnly(text).GetAwaiter().GetResult();
            }
        }

        private static void FinalizeCurrentSegment()
        {
            lock (transcriptLock)
            {
                string pending = transcriptAssembler.StartNewSegment();
                if (!string.IsNullOrWhiteSpace(pending))
                    pendingTextQueue.Enqueue(pending);

                if (Caption != null)
                {
                    Caption.FullTranscriptionText = transcriptAssembler.FullText;
                    TranscriptFileWriter.Write(transcriptAssembler.FullText);
                }
            }
        }
    }
}
