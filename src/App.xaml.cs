using System.Windows;

using LiveCaptionsTranscriber.utils;

namespace LiveCaptionsTranscriber
{
    public partial class App : Application
    {
        private static int cleanupStarted;

        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Transcriber.Setting?.Save();

            Task.Run(() => Transcriber.SyncLoop());
            Task.Run(() => Transcriber.TranscribeLoop());
            Task.Run(() => Transcriber.DisplayLoop());
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref cleanupStarted, 1) != 0)
                return;

            Transcriber.Shutdown();
            RawCaptionRecorder.Shutdown();

            if (Transcriber.Window != null)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Transcriber.Window);
                LiveCaptionsHandler.KillLiveCaptions(Transcriber.Window);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            OnProcessExit(this, EventArgs.Empty);
            base.OnExit(e);
        }
    }
}
