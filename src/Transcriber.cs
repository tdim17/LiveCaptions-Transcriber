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
        private static readonly List<TranscriptBoundary> transcriptBoundaries = new();
        private static readonly object transcriptLock = new();
        private static readonly object recordingStateLock = new();
        private static int recordingState = (int)RecordingState.Ready;
        private static int stopping;
        private static int shutdownStarted;


        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;
        public static RecordingState CurrentRecordingState =>
            (RecordingState)Volatile.Read(ref recordingState);
        public static string? CurrentTranscriptFile => TranscriptFileWriter.FilePath;

        public static bool TranscriptionOnlyMode { get; set; } = true;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranscriptionLogged;
        public static event Action? RecordingStateChanged;

        static Transcriber()
        {
            if (!File.Exists(models.Setting.FILE_PATH))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                if (CurrentRecordingState != RecordingState.Recording)
                {
                    Thread.Sleep(100);
                    continue;
                }

                if (Window == null)
                {
                    Thread.Sleep(100);
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
                if (CurrentRecordingState != RecordingState.Recording)
                    continue;
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
                    if (CurrentRecordingState != RecordingState.Recording)
                        continue;

                    transcriptUpdate = transcriptAssembler.Apply(transcriptSnapshot);
                    string formattedText = FormatTranscriptDocument();
                    Caption.FullTranscriptionText = formattedText;
                    TranscriptFileWriter.Write(formattedText);
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
                if (CurrentRecordingState == RecordingState.Recording && Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    try
                    {
                        AutomationElement replacement = PrepareLiveCaptions();
                        if (CurrentRecordingState == RecordingState.Recording)
                            Window = replacement;
                        else
                            LiveCaptionsHandler.KillLiveCaptions(replacement);
                        Caption.DisplayTranslatedCaption = "";
                    }
                    catch (Exception exception)
                    {
                        Caption.DisplayTranslatedCaption =
                            $"[WARNING] Unable to restart Live Captions: {exception.Message}";
                        await Task.Delay(1000);
                    }
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
                transcriptBoundaries.Clear();
                while (pendingTextQueue.TryDequeue(out _))
                {
                }
                Caption?.ClearFullTranscription();
                TranscriptFileWriter.Clear();
            }
        }

        public static void StartRecording()
        {
            if (!TryChangeState(
                    new[] { RecordingState.Ready, RecordingState.Stopped },
                    RecordingState.Starting))
                throw new InvalidOperationException("Recording cannot be started in the current state.");

            AutomationElement? preparedWindow = null;
            try
            {
                preparedWindow = PrepareLiveCaptions();
                lock (transcriptLock)
                {
                    transcriptAssembler.Reset();
                    transcriptBoundaries.Clear();
                    while (pendingTextQueue.TryDequeue(out _))
                    {
                    }
                    Caption?.ClearFullTranscription();
                    TranscriptFileWriter.BeginSession();
                    Window = preparedWindow;
                }
                ChangeState(RecordingState.Recording);
            }
            catch
            {
                if (preparedWindow != null)
                    TryKillLiveCaptions(preparedWindow);
                Window = null;
                TranscriptFileWriter.EndSession();
                ChangeState(RecordingState.Ready);
                throw;
            }
        }

        public static void PauseRecording()
        {
            if (!TryChangeState(
                    new[] { RecordingState.Recording },
                    RecordingState.Paused))
                throw new InvalidOperationException("Recording is not active.");

            CaptureFinalSnapshot();
            FinalizeCurrentSegment();
            AutomationElement? currentWindow = Window;
            Window = null;
            if (currentWindow != null)
                TryKillLiveCaptions(currentWindow);
        }

        public static void ResumeRecording()
        {
            if (!TryChangeState(
                    new[] { RecordingState.Paused },
                    RecordingState.Starting))
                throw new InvalidOperationException("Recording is not paused.");

            AutomationElement? preparedWindow = null;
            try
            {
                preparedWindow = PrepareLiveCaptions();
                lock (transcriptLock)
                {
                    string marker =
                        $"========== CONTINUE {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========";
                    transcriptBoundaries.Add(new TranscriptBoundary(
                        transcriptAssembler.FullText.Length,
                        marker));

                    string formattedText = FormatTranscriptDocument();
                    if (Caption != null)
                        Caption.FullTranscriptionText = formattedText;
                    TranscriptFileWriter.Write(formattedText);
                    Window = preparedWindow;
                }
                ChangeState(RecordingState.Recording);
            }
            catch
            {
                if (preparedWindow != null)
                    TryKillLiveCaptions(preparedWindow);
                Window = null;
                ChangeState(RecordingState.Paused);
                throw;
            }
        }

        public static void StopRecording()
        {
            RecordingState previousState;
            lock (recordingStateLock)
            {
                previousState = (RecordingState)recordingState;
                if (previousState is not (RecordingState.Recording or RecordingState.Paused))
                    throw new InvalidOperationException("There is no active recording to stop.");
                Volatile.Write(ref recordingState, (int)RecordingState.Stopped);
            }
            if (previousState == RecordingState.Recording)
            {
                CaptureFinalSnapshot();
                FinalizeCurrentSegment();
            }

            AutomationElement? currentWindow = Window;
            Window = null;
            if (currentWindow != null)
                TryKillLiveCaptions(currentWindow);
            TranscriptFileWriter.EndSession();
            RecordingStateChanged?.Invoke();
        }

        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
                return;

            Volatile.Write(ref stopping, 1);
            if (CurrentRecordingState == RecordingState.Recording)
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
                    string formattedText = FormatTranscriptDocument();
                    Caption.FullTranscriptionText = formattedText;
                    TranscriptFileWriter.Write(formattedText);
                }
            }
        }

        private static void CaptureFinalSnapshot()
        {
            AutomationElement? currentWindow = Window;
            if (currentWindow == null)
                return;

            try
            {
                string finalSnapshot = LiveCaptionsHandler.GetCaptions(currentWindow);
                if (string.IsNullOrEmpty(finalSnapshot))
                    return;

                RawCaptionRecorder.Record(finalSnapshot);
                lock (transcriptLock)
                    transcriptAssembler.Apply(finalSnapshot);
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        private static string FormatTranscriptDocument()
        {
            return TranscriptDocumentFormatter.Format(
                transcriptAssembler.FullText,
                transcriptBoundaries);
        }

        private static AutomationElement PrepareLiveCaptions()
        {
            AutomationElement preparedWindow = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(preparedWindow);
            LiveCaptionsHandler.HideLiveCaptions(preparedWindow);
            return preparedWindow;
        }

        private static bool TryChangeState(
            IReadOnlyCollection<RecordingState> allowedStates,
            RecordingState newState)
        {
            lock (recordingStateLock)
            {
                RecordingState current = (RecordingState)recordingState;
                if (!allowedStates.Contains(current))
                    return false;
                Volatile.Write(ref recordingState, (int)newState);
            }
            RecordingStateChanged?.Invoke();
            return true;
        }

        private static void ChangeState(RecordingState newState)
        {
            lock (recordingStateLock)
                Volatile.Write(ref recordingState, (int)newState);
            RecordingStateChanged?.Invoke();
        }

        private static void TryKillLiveCaptions(AutomationElement captionsWindow)
        {
            try
            {
                LiveCaptionsHandler.KillLiveCaptions(captionsWindow);
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
