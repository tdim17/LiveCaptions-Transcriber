using System.Diagnostics;
using System.Windows.Automation;

namespace LiveCaptionsTranscriber.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        private const int MenuElementTimeoutMilliseconds = 1500;

        private static AutomationElement? captionsTextBlock = null;

        public static AutomationElement LaunchLiveCaptions()
        {
            // Init
            captionsTextBlock = null;
            KillAllProcessesByPName(PROCESS_NAME);
            var process = Process.Start(PROCESS_NAME);

            // Search for window
            AutomationElement? window = null;
            for (int attemptCount = 0;
                 window == null || window.Current.ClassName.CompareTo("LiveCaptionsDesktopWindow") != 0;
                 attemptCount++)
            {
                window = FindWindowByPId(process.Id);
                if (attemptCount > 10000)
                    throw new Exception("Failed to launch LiveCaptions!");
            }

            return window;
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            captionsTextBlock = null;
            // Search for process
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
            var process = Process.GetProcessById(processId);

            // Kill process
            process.Kill();
            process.WaitForExit();
        }

        public static void HideLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
        }

        public static void RestoreLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle & ~WindowsAPI.WS_EX_TOOLWINDOW);
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            WindowsAPI.SetForegroundWindow(hWnd);
        }

        public static void FixLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);

            RECT rect;
            if (!WindowsAPI.GetWindowRect(hWnd, out rect))
                throw new Exception("Unable to get the window rectangle of LiveCaptions!");
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            int x = rect.Left;
            int y = rect.Top;

            bool isSuccess = true;
            if (x < 0 || y < 0 || width < 100 || height < 100)
                isSuccess = WindowsAPI.MoveWindow(hWnd, 800, 600, 600, 200, true);
            if (!isSuccess)
                throw new Exception("Failed to fix LiveCaptions!");
        }

        public static string GetCaptions(AutomationElement window)
        {
            if (captionsTextBlock == null)
                captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
            try
            {
                return captionsTextBlock?.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                throw;
            }
        }

        private static AutomationElement FindWindowByPId(int processId)
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
        }

        public static AutomationElement? FindElementByAId(
            AutomationElement window, string automationId, CancellationToken token = default)
        {
            try
            {
                PropertyCondition condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                return window.FindFirst(TreeScope.Descendants, condition);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        public static void PrintAllElementsAId(AutomationElement window)
        {
            var treeWalker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(window);

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (!string.IsNullOrEmpty(element.Current.AutomationId))
                    Console.WriteLine(element.Current.AutomationId);

                var child = treeWalker.GetFirstChild(element);
                while (child != null)
                {
                    stack.Push(child);
                    child = treeWalker.GetNextSibling(child);
                }
            }
        }

        public static bool ClickSettingsButton(AutomationElement window)
        {
            var settingsButton = FindElementByAId(window, "SettingsButton");
            if (settingsButton != null)
            {
                var invokePattern = settingsButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                if (invokePattern != null)
                {
                    invokePattern.Invoke();
                    return true;
                }
            }
            return false;
        }

        public static void SetPreferences(
            AutomationElement window,
            bool? includeMicrophoneAudio = null,
            bool? filterProfanity = null)
        {
            if (includeMicrophoneAudio == null && filterProfanity == null)
                return;

            AutomationElement settingsButton = WaitForElementByAId(
                window,
                "SettingsButton");
            InvokeOrExpand(settingsButton);

            AutomationElement preferencesButton = WaitForElementByAId(
                window,
                "PreferencesButton");
            InvokeOrExpand(preferencesButton);

            if (includeMicrophoneAudio != null)
            {
                AutomationElement microphoneItem = WaitForElementByAId(
                    window,
                    "MicrophoneMenuFlyoutItem");
                SetToggleState(microphoneItem, includeMicrophoneAudio.Value);
            }

            if (filterProfanity != null)
            {
                // Toggling the first item can close the flyout. Re-open it before
                // applying a second preference in the same operation.
                if (includeMicrophoneAudio != null)
                {
                    settingsButton = WaitForElementByAId(window, "SettingsButton");
                    InvokeOrExpand(settingsButton);
                    preferencesButton = WaitForElementByAId(window, "PreferencesButton");
                    InvokeOrExpand(preferencesButton);
                }

                AutomationElement profanityItem = WaitForElementByAId(
                    window,
                    "ProfanityFilterMenuFlyoutItem");
                SetToggleState(profanityItem, filterProfanity.Value);
            }

            // Keep the Microsoft window hidden after an option is changed.
            HideLiveCaptions(window);
        }

        private static AutomationElement WaitForElementByAId(
            AutomationElement window,
            string automationId)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < MenuElementTimeoutMilliseconds)
            {
                AutomationElement? element = FindElementByAId(window, automationId);
                if (element != null)
                    return element;
                Thread.Sleep(25);
            }

            throw new InvalidOperationException(
                $"Windows Live Captions control '{automationId}' was not found.");
        }

        private static void InvokeOrExpand(AutomationElement element)
        {
            if (element.TryGetCurrentPattern(
                    ExpandCollapsePattern.Pattern,
                    out object? expandPatternObject) &&
                expandPatternObject is ExpandCollapsePattern expandPattern)
            {
                if (expandPattern.Current.ExpandCollapseState !=
                    ExpandCollapseState.Expanded)
                    expandPattern.Expand();
                return;
            }

            if (element.TryGetCurrentPattern(
                    InvokePattern.Pattern,
                    out object? invokePatternObject) &&
                invokePatternObject is InvokePattern invokePattern)
            {
                invokePattern.Invoke();
                return;
            }

            throw new InvalidOperationException(
                $"Windows Live Captions control '{element.Current.AutomationId}' cannot be invoked.");
        }

        private static void SetToggleState(AutomationElement element, bool enabled)
        {
            if (element.TryGetCurrentPattern(
                    TogglePattern.Pattern,
                    out object? togglePatternObject) &&
                togglePatternObject is TogglePattern togglePattern)
            {
                bool isEnabled = togglePattern.Current.ToggleState == ToggleState.On;
                if (isEnabled != enabled)
                    togglePattern.Toggle();
                return;
            }

            // Some Windows builds expose toggle menu items through InvokePattern
            // and SelectionItemPattern rather than TogglePattern.
            if (element.TryGetCurrentPattern(
                    SelectionItemPattern.Pattern,
                    out object? selectionPatternObject) &&
                selectionPatternObject is SelectionItemPattern selectionPattern)
            {
                bool isSelected = selectionPattern.Current.IsSelected;
                if (isSelected != enabled)
                    InvokeOrExpand(element);
                return;
            }

            throw new InvalidOperationException(
                $"Windows Live Captions control '{element.Current.AutomationId}' does not expose a toggle state.");
        }

        private static void KillAllProcessesByPName(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
                return;
            foreach (Process process in processes)
            {
                process.Kill();
                process.WaitForExit();
            }
        }
    }
}
