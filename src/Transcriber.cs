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
        private static readonly TranscriptSessionDocument sessionDocument = new();
        private static readonly object transcriptLock = new();
        private static readonly object recordingStateLock = new();
        private static readonly object liveCaptionsLock = new();
        private static int recordingState = (int)RecordingState.Ready;
        private static bool microphoneAudioRequested;
        private static string pausedBaselineSnapshot = string.Empty;
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
        public static bool MicrophoneAudioRequested
        {
            get
            {
                lock (recordingStateLock)
                    return microphoneAudioRequested;
            }
        }

        public static bool TranscriptionOnlyMode { get; set; } = true;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranscriptionLogged;
        public static event Action? RecordingStateChanged;
        public static event Action? LiveCaptionsOptionsChanged;

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
                RecordingState currentState = CurrentRecordingState;
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
                }
                catch (ElementNotAvailableException)
                {
                    Caption.LiveCaptionPreviewText = string.Empty;
                    if (currentState == RecordingState.Recording)
                        FreezeCurrentSegment();
                    Window = null;
                    continue;
                }
                currentState = CurrentRecordingState;
                Caption.LiveCaptionPreviewText = fullText;
                if (currentState is RecordingState.Recording or RecordingState.Paused)
                    RawCaptionRecorder.Record(fullText);
                if (currentState == RecordingState.Paused)
                {
                    lock (transcriptLock)
                        pausedBaselineSnapshot = fullText;
                    Thread.Sleep(25);
                    continue;
                }
                if (currentState != RecordingState.Recording)
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

                    transcriptUpdate = sessionDocument.ApplySnapshot(transcriptSnapshot);
                    string formattedText = sessionDocument.FullDocument;
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
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    try
                    {
                        EnsureLiveCaptions();
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

        public static void ApplyEditedTranscript(string editedDocument)
        {
            if (CurrentRecordingState != RecordingState.Paused)
                throw new InvalidOperationException(
                    "The transcript can only be edited while recording is paused.");

            lock (transcriptLock)
            {
                if (CurrentRecordingState != RecordingState.Paused)
                    throw new InvalidOperationException(
                        "Recording is no longer paused.");

                sessionDocument.ReplaceFrozenDocument(editedDocument);
                string document = sessionDocument.FullDocument;
                if (Caption != null)
                    Caption.FullTranscriptionText = document;
                TranscriptFileWriter.Write(document);
            }
        }

        public static void StartRecording()
        {
            if (!TryChangeState(
                    new[] { RecordingState.Ready, RecordingState.Stopped },
                    RecordingState.Starting))
                throw new InvalidOperationException("Recording cannot be started in the current state.");

            try
            {
                AutomationElement preparedWindow = EnsureLiveCaptions();
                lock (transcriptLock)
                {
                    sessionDocument.Reset();
                    pausedBaselineSnapshot = string.Empty;
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

            string finalSnapshot = CaptureFinalSnapshot();
            FreezeCurrentSegment();
            lock (transcriptLock)
                pausedBaselineSnapshot = finalSnapshot;
        }

        public static void ResumeRecording()
        {
            if (!TryChangeState(
                    new[] { RecordingState.Paused },
                    RecordingState.Starting))
                throw new InvalidOperationException("Recording is not paused.");

            try
            {
                AutomationElement preparedWindow = EnsureLiveCaptions();

                string baselineSnapshot;
                try
                {
                    baselineSnapshot = LiveCaptionsHandler.GetCaptions(preparedWindow);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    preparedWindow = EnsureLiveCaptions();
                    baselineSnapshot = string.Empty;
                }

                lock (transcriptLock)
                {
                    if (string.IsNullOrEmpty(baselineSnapshot))
                        baselineSnapshot = pausedBaselineSnapshot;

                    string marker =
                        $"========== CONTINUE {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========";
                    sessionDocument.ResumeWithBoundary(marker, baselineSnapshot);
                    pausedBaselineSnapshot = string.Empty;

                    string formattedText = sessionDocument.FullDocument;
                    if (Caption != null)
                        Caption.FullTranscriptionText = formattedText;
                    TranscriptFileWriter.Write(formattedText);
                    Window = preparedWindow;
                }
                ChangeState(RecordingState.Recording);
            }
            catch
            {
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
                FreezeCurrentSegment();
            }

            TranscriptFileWriter.EndSession();
            RecordingStateChanged?.Invoke();
            LiveCaptionsOptionsChanged?.Invoke();
        }

        public static void StopRecordingAs(string destinationPath)
        {
            StopRecording();
            TranscriptFileWriter.MoveCompletedFile(destinationPath);
            RecordingStateChanged?.Invoke();
        }

        public static void ToggleMicrophoneAudio()
        {
            bool requested;
            AutomationElement? currentWindow;

            lock (recordingStateLock)
            {
                if ((RecordingState)recordingState == RecordingState.Starting)
                    throw new InvalidOperationException(
                        "Wait until Windows Live Captions finishes starting.");

                requested = !microphoneAudioRequested;
                currentWindow = Window;
            }

            // Live Captions stays active for the application's lifetime, so this
            // always represents an explicit user command against the live engine.
            if (currentWindow != null)
            {
                LiveCaptionsHandler.SetPreferences(
                    currentWindow,
                    includeMicrophoneAudio: requested);
            }

            lock (recordingStateLock)
                microphoneAudioRequested = requested;
            LiveCaptionsOptionsChanged?.Invoke();
        }

        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
                return;

            Volatile.Write(ref stopping, 1);
            if (CurrentRecordingState == RecordingState.Recording)
            {
                CaptureFinalSnapshot();
                FreezeCurrentSegment();
            }
            TranscriptFileWriter.EndSession();

            while (pendingTextQueue.TryDequeue(out string? text))
            {
                if (!string.IsNullOrWhiteSpace(text))
                    LogOnly(text).GetAwaiter().GetResult();
            }
        }

        private static void FreezeCurrentSegment()
        {
            lock (transcriptLock)
            {
                TranscriptFreezeResult result = sessionDocument.FreezeActiveSegment();
                if (!string.IsNullOrWhiteSpace(result.PendingText))
                    pendingTextQueue.Enqueue(result.PendingText);

                if (Caption != null)
                {
                    Caption.FullTranscriptionText = result.Document;
                    TranscriptFileWriter.Write(result.Document);
                }
            }
        }

        private static string CaptureFinalSnapshot()
        {
            AutomationElement? currentWindow = Window;
            if (currentWindow == null)
                return string.Empty;

            try
            {
                string finalSnapshot = LiveCaptionsHandler.GetCaptions(currentWindow);
                if (string.IsNullOrEmpty(finalSnapshot))
                    return string.Empty;

                RawCaptionRecorder.Record(finalSnapshot);
                lock (transcriptLock)
                    sessionDocument.ApplySnapshot(finalSnapshot);
                return finalSnapshot;
            }
            catch (ElementNotAvailableException)
            {
                return string.Empty;
            }
        }

        private static AutomationElement EnsureLiveCaptions()
        {
            lock (liveCaptionsLock)
            {
                if (Window != null)
                {
                    try
                    {
                        _ = Window.Current.Name;
                        return Window;
                    }
                    catch (ElementNotAvailableException)
                    {
                        Window = null;
                    }
                }

                AutomationElement preparedWindow = PrepareLiveCaptions();
                Window = preparedWindow;
                return preparedWindow;
            }
        }

        private static AutomationElement PrepareLiveCaptions()
        {
            AutomationElement preparedWindow = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(preparedWindow);
            if (MicrophoneAudioRequested)
            {
                LiveCaptionsHandler.SetPreferences(
                    preparedWindow,
                    includeMicrophoneAudio: true);
            }
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

    }
}
