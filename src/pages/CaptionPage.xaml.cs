using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Input;
using Microsoft.Win32;

using LiveCaptionsTranscriber.utils;
using LiveCaptionsTranscriber.models;

namespace LiveCaptionsTranscriber
{
    public partial class CaptionPage : Page
    {
        public const int CARD_HEIGHT = 110;

        private static CaptionPage instance;
        private MainWindow? ownerWindow;
        private bool isEditing;
        private string editOriginalText = string.Empty;
        private string lastEditText = string.Empty;
        private bool suppressEditHistory;
        private readonly Stack<string> editUndoHistory = new();
        public static CaptionPage Instance => instance;

        public CaptionPage()
        {
            InitializeComponent();
            DataContext = Transcriber.Caption;
            instance = this;

            Loaded += (s, e) =>
            {
                Transcriber.Caption.PropertyChanged += TranslatedChanged;
                Transcriber.RecordingStateChanged += RecordingStateChanged;
                ownerWindow = Window.GetWindow(this) as MainWindow;
                if (ownerWindow != null)
                {
                    ownerWindow.RootNavigation.SizeChanged += RootNavigation_SizeChanged;
                    ConstrainToNavigationViewport(
                        ownerWindow.RootNavigation.ActualHeight);
                }
                RefreshRecordingControls();
            };
            Unloaded += (s, e) =>
            {
                Transcriber.Caption.PropertyChanged -= TranslatedChanged;
                Transcriber.RecordingStateChanged -= RecordingStateChanged;
                if (ownerWindow != null)
                {
                    ownerWindow.RootNavigation.SizeChanged -= RootNavigation_SizeChanged;
                    ownerWindow = null;
                }
            };
        }

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            // Auto-scroll to bottom when new transcription is added
            if (!isEditing &&
                e.PropertyName == nameof(Transcriber.Caption.FullTranscriptionText))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TranscriptionScrollViewer.ScrollToBottom();
                }), DispatcherPriority.Background);
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(Transcriber.Caption.FullTranscriptionText))
                {
                    Clipboard.SetText(Transcriber.Caption.FullTranscriptionText);
                    ShowTemporaryMessage("Copied to clipboard!");
                }
                else
                {
                    ShowTemporaryMessage("No transcription to copy");
                }
            }
            catch
            {
                ShowTemporaryMessage("Failed to copy");
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isEditing)
                return;

            FullTranscription.Focus();
            FullTranscription.SelectAll();
            FullTranscription.SelectedText = string.Empty;
            FullTranscription.CaretIndex = 0;
            RefreshRecordingControls();
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (isEditing || Transcriber.CurrentRecordingState != RecordingState.Paused)
                return;

            editOriginalText = Transcriber.Caption?.FullTranscriptionText ?? string.Empty;
            suppressEditHistory = true;
            FullTranscription.SetCurrentValue(TextBox.TextProperty, editOriginalText);
            suppressEditHistory = false;
            FullTranscription.IsReadOnly = false;
            editUndoHistory.Clear();
            lastEditText = editOriginalText;
            isEditing = true;
            FullTranscription.Focus();
            FullTranscription.CaretIndex = FullTranscription.Text.Length;
            RefreshRecordingControls();
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isEditing || editUndoHistory.Count == 0)
                return;

            string previousText = editUndoHistory.Pop();
            suppressEditHistory = true;
            FullTranscription.SetCurrentValue(TextBox.TextProperty, previousText);
            suppressEditHistory = false;
            lastEditText = previousText;
            FullTranscription.Focus();
            RefreshRecordingControls();
        }

        private void FullTranscription_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isEditing && !suppressEditHistory &&
                !string.Equals(FullTranscription.Text, lastEditText, StringComparison.Ordinal))
            {
                editUndoHistory.Push(lastEditText);
                lastEditText = FullTranscription.Text;
            }

            if (UndoButton != null)
                UndoButton.IsEnabled = isEditing && editUndoHistory.Count > 0;
        }

        private void FullTranscription_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (isEditing && e.Key == Key.Z &&
                Keyboard.Modifiers == ModifierKeys.Control)
            {
                UndoButton_Click(sender, e);
                e.Handled = true;
            }
        }

        private async void ApplyEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isEditing)
                return;

            string editedText = FullTranscription.Text;
            try
            {
                await Task.Run(() => Transcriber.ApplyEditedTranscript(editedText));
                ExitEditMode(restoreOriginal: false);
                ShowTemporaryMessage("Edits saved");
            }
            catch (Exception exception)
            {
                RecordingStatusText.Text = $"Unable to apply edits: {exception.Message}";
            }
        }

        private void CancelEditButton_Click(object sender, RoutedEventArgs e)
        {
            if (isEditing)
                ExitEditMode(restoreOriginal: true);
        }

        private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            if (isEditing ||
                Transcriber.CurrentRecordingState is not
                    (RecordingState.Recording or RecordingState.Paused))
                return;

            string? currentPath = Transcriber.CurrentTranscriptFile;
            var dialog = new SaveFileDialog
            {
                AddExtension = true,
                CheckPathExists = true,
                DefaultExt = ".txt",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                InitialDirectory = AppPaths.TranscriptsDirectory,
                FileName = string.IsNullOrEmpty(currentPath)
                    ? $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_transcript.txt"
                    : Path.GetFileName(currentPath),
                OverwritePrompt = true,
                Title = "Save transcript as"
            };

            if (dialog.ShowDialog() != true)
                return;

            await RunRecordingCommand(
                () => Transcriber.StopRecordingAs(dialog.FileName),
                "Unable to save transcript");
        }

        private void OpenResultsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.TranscriptsDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppPaths.TranscriptsDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                RecordingStatusText.Text =
                    $"Unable to open Results_txt: {exception.Message}";
            }
        }

        private void ExitEditMode(bool restoreOriginal)
        {
            isEditing = false;
            FullTranscription.IsReadOnly = true;
            suppressEditHistory = true;
            FullTranscription.SetCurrentValue(
                TextBox.TextProperty,
                restoreOriginal
                    ? editOriginalText
                    : Transcriber.Caption?.FullTranscriptionText ?? string.Empty);
            suppressEditHistory = false;
            editUndoHistory.Clear();
            editOriginalText = string.Empty;
            lastEditText = string.Empty;
            RefreshRecordingControls();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            await RunRecordingCommand(Transcriber.StartRecording, "Unable to start recording");
        }

        private async void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            await RunRecordingCommand(Transcriber.PauseRecording, "Unable to pause recording");
        }

        private async void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            await RunRecordingCommand(Transcriber.ResumeRecording, "Unable to resume recording");
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await RunRecordingCommand(Transcriber.StopRecording, "Unable to stop recording");
        }

        private async Task RunRecordingCommand(Action command, string errorPrefix)
        {
            try
            {
                await Task.Run(command);
            }
            catch (Exception exception)
            {
                RecordingStatusText.Text = $"{errorPrefix}: {exception.Message}";
            }
            RefreshRecordingControls();
        }

        private void RecordingStateChanged()
        {
            Dispatcher.BeginInvoke(
                new Action(RefreshRecordingControls),
                DispatcherPriority.Normal);
        }

        private void RootNavigation_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ConstrainToNavigationViewport(e.NewSize.Height);
        }

        private void ConstrainToNavigationViewport(double viewportHeight)
        {
            if (viewportHeight > 0)
                Height = viewportHeight;
        }

        private void RefreshRecordingControls()
        {
            RecordingState state = Transcriber.CurrentRecordingState;
            bool isStarting = state == RecordingState.Starting;

            StartButton.IsEnabled = !isEditing &&
                state is (RecordingState.Ready or RecordingState.Stopped);
            PauseButton.IsEnabled = !isEditing && state == RecordingState.Recording;
            ResumeButton.IsEnabled = !isEditing && state == RecordingState.Paused;
            StopButton.IsEnabled = !isEditing &&
                state is (RecordingState.Recording or RecordingState.Paused);
            CopyButton.IsEnabled = !isStarting;
            ClearButton.IsEnabled = isEditing;
            EditButton.IsEnabled = !isEditing && state == RecordingState.Paused;
            UndoButton.IsEnabled = isEditing && editUndoHistory.Count > 0;
            SaveAsButton.IsEnabled = !isEditing &&
                state is (RecordingState.Recording or RecordingState.Paused);
            ApplyEditButton.Visibility = isEditing
                ? Visibility.Visible
                : Visibility.Collapsed;
            CancelEditButton.Visibility = isEditing
                ? Visibility.Visible
                : Visibility.Collapsed;
            FullTranscription.IsReadOnly = !isEditing;

            RecordingStatusText.Text = state switch
            {
                RecordingState.Ready => "READY — press Start Recording",
                RecordingState.Starting => "STARTING",
                RecordingState.Recording => "●  RECORDING",
                RecordingState.Paused => "PAUSED",
                RecordingState.Stopped => "RECORDED",
                _ => state.ToString()
            };
            RecordingStatusBanner.ToolTip = state == RecordingState.Paused
                ? "Live Captions remain active; text is not recorded"
                : null;

            if (state == RecordingState.Recording)
            {
                RecordingStatusBanner.Background =
                    new SolidColorBrush(Color.FromRgb(230, 0, 0));
                RecordingStatusText.Foreground = Brushes.White;
            }
            else if (state == RecordingState.Paused)
            {
                RecordingStatusBanner.Background =
                    new SolidColorBrush(Color.FromRgb(255, 193, 7));
                RecordingStatusText.Foreground = Brushes.Black;
            }
            else
            {
                RecordingStatusBanner.Background = Brushes.Transparent;
                RecordingStatusText.ClearValue(TextBlock.ForegroundProperty);
            }

            string? filePath = Transcriber.CurrentTranscriptFile;
            TranscriptFileText.Text = string.IsNullOrEmpty(filePath)
                ? "No transcript file yet"
                : Path.GetFileName(filePath);
            TranscriptFileText.ToolTip = filePath;
            OpenResultsButton.ToolTip = string.IsNullOrEmpty(filePath)
                ? AppPaths.TranscriptsDirectory
                : filePath;
        }

        private async void ShowTemporaryMessage(string message)
        {
            object? originalToolTip = CopyButton.ToolTip;
            CopyButton.ToolTip = message;
            await Task.Delay(1500);
            CopyButton.ToolTip = originalToolTip;
        }

        public void CollapseTranslatedCaption(bool isCollapsed)
        {
            // Not needed in transcription-only mode
        }


    }
}
