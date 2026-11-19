using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;

using LiveCaptionsTranscriber.utils;
using LiveCaptionsTranscriber.models;

namespace LiveCaptionsTranscriber
{
    public partial class CaptionPage : Page
    {
        public const int CARD_HEIGHT = 110;

        private static CaptionPage instance;
        private MainWindow? ownerWindow;
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

        private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                try
                {
                    Clipboard.SetText(textBlock.Text);
                    textBlock.ToolTip = "Copied!";
                }
                catch
                {
                    textBlock.ToolTip = "Error to Copy";
                }
                await Task.Delay(500);
                textBlock.ToolTip = "Click to Copy";
            }
        }

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            // Auto-scroll to bottom when new transcription is added
            if (e.PropertyName == nameof(Transcriber.Caption.FullTranscriptionText))
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
            try
            {
                Transcriber.ClearTranscription();
                ShowTemporaryMessage("Transcription cleared");
            }
            catch
            {
                ShowTemporaryMessage("Failed to clear");
            }
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

            StartButton.IsEnabled = state is RecordingState.Ready or RecordingState.Stopped;
            PauseButton.IsEnabled = state == RecordingState.Recording;
            ResumeButton.IsEnabled = state == RecordingState.Paused;
            StopButton.IsEnabled = state is RecordingState.Recording or RecordingState.Paused;
            ClearButton.IsEnabled = !isStarting;

            RecordingStatusText.Text = state switch
            {
                RecordingState.Ready => "Ready — press Start Recording",
                RecordingState.Starting => "Starting Live Captions...",
                RecordingState.Recording => "●  RECORDING",
                RecordingState.Paused => "Paused — Live Captions active; text is excluded",
                RecordingState.Stopped => "Stopped — file saved",
                _ => state.ToString()
            };

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
                : filePath;
        }

        private async void ShowTemporaryMessage(string message)
        {
            var originalText = CopyButton.Content;
            CopyButton.Content = message;
            await Task.Delay(1500);
            CopyButton.Content = originalText;
        }

        public void CollapseTranslatedCaption(bool isCollapsed)
        {
            // Not needed in transcription-only mode
        }


    }
}
