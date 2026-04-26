using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.CmdPal.Common.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace AgentStatusTaskbar
{
    public sealed partial class MainWindow : WindowEx,
        IRecipient<TaskbarRestartMessage>,
        IRecipient<QuitMessage>
    {
        private readonly uint WM_TASKBAR_RESTART;
        private readonly HWND _hwnd;
        private readonly TrayIconService _trayIconService = new();
        private readonly AppWindow _appWindow;
        private readonly Tasklist _tasklist;
        private readonly TaskbarWidgetDetector _widgetDetector = new();

        // Constants for Windows messages related to display changes
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_DESTROY = 0x0002;
        private static readonly TimeSpan ExplorerRetryDelay = TimeSpan.FromSeconds(1);
        private static readonly HWND HWND_TOPMOST = new HWND(-1);
        private const uint GW_HWNDPREV = 3;

        // Store the original WndProc
        private WNDPROC? _originalWndProc;
        private WNDPROC? _hotkeyWndProc;
        private nint _originalWndProcPtr;
        private bool _wndProcRestored;

        // Debouncer to throttle UpdateLayoutForDPI calls
        private readonly DispatcherQueueTimer _updateLayoutDebouncer;
        private readonly DispatcherQueueTimer _updateTaskbarButtonsTimer;
        private readonly DispatcherQueueTimer _explorerRecoveryTimer;
        private readonly DispatcherQueueTimer _topmostWatchTimer;

        private double _lastContentSpace = 0;
        private bool _isClosing;
        private bool _isExplicitQuit;
        private DateTimeOffset _explorerInteractionResumeAt;

        private readonly BandsItemsControl? _bandsControl;

        public MainWindow()
        {
            InitializeComponent();

            WM_TASKBAR_RESTART = PInvoke.RegisterWindowMessage("TaskbarCreated");

            // Comment this out if you don't want to use the extensible deskbands
            _bandsControl = DeskbandsControl;

            _hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));

            // Initialize debouncer with 300ms delay to throttle UpdateLayoutForDPI calls
            _updateLayoutDebouncer = DispatcherQueue.CreateTimer();

            // Timer to re-layout based on available space in taskbar
            _updateTaskbarButtonsTimer = DispatcherQueue.CreateTimer();
            _updateTaskbarButtonsTimer.Tick += (s, e) => ClipWindow(onlyIfButtonsChanged: true).ConfigureAwait(false);
            _updateTaskbarButtonsTimer.Interval = TimeSpan.FromMilliseconds(500);
            _updateTaskbarButtonsTimer.Start();

            _explorerRecoveryTimer = DispatcherQueue.CreateTimer();
            _explorerRecoveryTimer.IsRepeating = false;
            _explorerRecoveryTimer.Tick += (_, _) =>
            {
                _explorerRecoveryTimer.Stop();
                if (_isClosing)
                {
                    return;
                }

                _updateTaskbarButtonsTimer.Start();
                MoveToTaskbar();
            };

            // Polling z-order watcher. We tried a reactive
            // SetWinEventHook(EVENT_OBJECT_REORDER) approach but it caused the
            // overlay to be hidden after taskbar interaction in ways we
            // couldn't reliably recover from. Polling at 500 ms with an
            // edge-triggered SetWindowPos (only when actually covered, and
            // only by inserting above the offender within the topmost band)
            // is the last known-good behaviour.
            _topmostWatchTimer = DispatcherQueue.CreateTimer();
            _topmostWatchTimer.Interval = TimeSpan.FromMilliseconds(500);
            _topmostWatchTimer.Tick += (_, _) => RestoreTopmostIfCovered();
            _topmostWatchTimer.Start();

            this.VisibilityChanged += MainWindow_VisibilityChanged;
            // this.ItemsBar.SizeChanged += ItemsBar_SizeChanged;
            //this.Root.SizeChanged += ItemsBar_SizeChanged;
            this.MainContent.SizeChanged += ItemsBar_SizeChangedAsync;

            WeakReferenceMessenger.Default.Register<TaskbarRestartMessage>(this);
            WeakReferenceMessenger.Default.Register<QuitMessage>(this);

            _appWindow = this.AppWindow;

            _tasklist = new Tasklist();

            // Set up custom window procedure to listen for display changes
            // LOAD BEARING: If you don't stick the pointer to HotKeyPrc into a
            // member (and instead like, use a local), then the pointer we marshal
            // into the WindowLongPtr will be useless after we leave this function,
            // and our **WindProc will explode**.
            _hotkeyWndProc = CustomWndProc;
            nint hotKeyPrcPointer = Marshal.GetFunctionPointerForDelegate(_hotkeyWndProc);
            _originalWndProcPtr = PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_WNDPROC, hotKeyPrcPointer);
            if (_originalWndProcPtr != 0)
            {
                _originalWndProc = Marshal.GetDelegateForFunctionPointer<WNDPROC>(_originalWndProcPtr);
            }

            ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar?.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            MoveToTaskbar();
            _trayIconService.SetupTrayIcon(true);
        }

        private void ItemsBar_SizeChangedAsync(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            if (_isClosing)
            {
                return;
            }

            ClipWindow().ConfigureAwait(false);
        }

        private void MainWindow_VisibilityChanged(object sender, Microsoft.UI.Xaml.WindowVisibilityChangedEventArgs args)
        {
            MoveToTaskbar();
        }
        private LRESULT CustomWndProc(HWND hwnd, uint uMsg, WPARAM wParam, LPARAM lParam)
        {
            if (uMsg == WM_DISPLAYCHANGE)
            {
                ScheduleLayoutRefresh(TimeSpan.FromSeconds(1));
            }
            else if (uMsg == WM_SETTINGCHANGE && wParam == (uint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWORKAREA)
            {
                ScheduleLayoutRefresh(TimeSpan.FromSeconds(1));
            }
            else if (uMsg == WM_TASKBAR_RESTART)
            {
                ScheduleLayoutRefresh(TimeSpan.FromSeconds(2));
            }
            else if (uMsg == WM_DESTROY)
            {
                if (!_isExplicitQuit)
                {
                    // XAML occasionally tries to tear us down on lifecycle
                    // events (focus/visibility/backdrop). Swallow it so the
                    // taskbar overlay keeps living instead of being recreated
                    // in a flicker loop.
                    return (LRESULT)0;
                }

                PrepareForClose();
            }

            return _originalWndProc != null
                ? PInvoke.CallWindowProc(_originalWndProc, hwnd, uMsg, wParam, lParam)
                : (LRESULT)0;
        }

        private void ScheduleLayoutRefresh(TimeSpan delay)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ScheduleExplorerRecovery(delay);
                TriggerDebouncedLayoutUpdate();
            });
        }

        private async Task UpdateLayoutForDPI()
        {
            if (_isClosing || IsExplorerInteractionSuspended)
            {
                return;
            }

            MoveToTaskbar();

            await Task.Delay(200);
            if (_isClosing)
            {
                return;
            }

            MainContent.Padding = new Thickness(1);
            await Task.Delay(10);
            if (_isClosing)
            {
                return;
            }

            MainContent.Padding = new Thickness(0);
        }

        private void TriggerDebouncedLayoutUpdate()
        {
            _updateLayoutDebouncer.Debounce(
                () =>
                {
                    _ = UpdateLayoutForDPI();
                },
                interval: TimeSpan.FromMilliseconds(200),
                immediate: false);

        }

        private bool IsExplorerInteractionSuspended => DateTimeOffset.UtcNow < _explorerInteractionResumeAt;

        private void ScheduleExplorerRecovery(TimeSpan delay)
        {
            if (_isClosing)
            {
                return;
            }

            _explorerInteractionResumeAt = DateTimeOffset.UtcNow.Add(delay);
            _updateTaskbarButtonsTimer.Stop();
            _explorerRecoveryTimer.Stop();
            _explorerRecoveryTimer.Interval = delay;
            _explorerRecoveryTimer.Start();
        }

        private void MoveToTaskbar()
        {
            if (_appWindow is null || _isClosing || IsExplorerInteractionSuspended)
            {
                return;
            }


            HWND thisWindow = _hwnd;
            if (thisWindow == HWND.Null)
            {
                return;
            }

            HWND taskbarWindow = PInvoke.FindWindow("Shell_TrayWnd", null);
            if (taskbarWindow.IsNull || !PInvoke.GetWindowRect(taskbarWindow, out RECT taskbarRect))
            {
                ScheduleExplorerRecovery(ExplorerRetryDelay);
                return;
            }

            // Keep the window as a top-level WS_EX_TOPMOST overlay rather
            // than parenting it into Shell_TrayWnd. Parenting is cleaner
            // visually (no z-order fight) but couples our lifetime to
            // Explorer/taskbar — display-topology changes destroy our HWND
            // and can destabilize Explorer itself. The overlay approach is
            // resilient to monitor connect/disconnect at the cost of a
            // small z-order watchdog (RestoreTopmostIfCovered).
            WINDOW_STYLE oldStyle = (WINDOW_STYLE)PInvoke.GetWindowLong(thisWindow, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            WINDOW_STYLE overlayStyle = (oldStyle | WINDOW_STYLE.WS_POPUP) & ~WINDOW_STYLE.WS_CHILD;
            overlayStyle &= ~(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_THICKFRAME);
            PInvoke.SetWindowLong(thisWindow, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (int)overlayStyle);

            WINDOW_EX_STYLE exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(thisWindow, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            exStyle |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST;
            exStyle &= ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
            PInvoke.SetWindowLong(thisWindow, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle);
            PInvoke.SetParent(thisWindow, HWND.Null);

            PInvoke.SetWindowRgn(_hwnd, HRGN.Null, true);

            PInvoke.SetWindowPos(thisWindow,
                         HWND_TOPMOST,
                         taskbarRect.left,
                         taskbarRect.top,
                         taskbarRect.Width,
                         taskbarRect.Height,
                         SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

            _overlayInitialized = true;
            ClipWindow().ConfigureAwait(false);
        }

        private bool _overlayInitialized;

        private static bool TryGetTaskbarRect(out RECT taskbarRect)
        {
            taskbarRect = default;

            HWND taskbarWindow = PInvoke.FindWindow("Shell_TrayWnd", null);
            if (taskbarWindow.IsNull)
            {
                return false;
            }

            return PInvoke.GetWindowRect(taskbarWindow, out taskbarRect);
        }


        private bool UpdateTaskbarButtons()
        {
            if (_isClosing || IsExplorerInteractionSuspended)
            {
                return false;
            }

            _tasklist.Update();
            float scaleFactor = this.GetDpiForWindow() / 96.0f;

            List<TasklistButton> buttons = _tasklist.GetButtons();
            int maxRightInPixels = 0;
            int totalWidth = 0;
            string lastButton = string.Empty;
            foreach (TasklistButton button in buttons)
            {
                totalWidth += button.Width;
                int right = button.X + button.Width;
                if (right > maxRightInPixels)
                {
                    maxRightInPixels = right;
                    lastButton = button.Name;
                }
            }

            HWND taskBarHwnd = PInvoke.FindWindow("Shell_TrayWnd", null);
            if (taskBarHwnd.IsNull)
            {
                return false;
            }

            HWND notificationHwnd = PInvoke.FindWindowEx(taskBarHwnd, HWND.Null, "TrayNotifyWnd", null);
            RECT trayRect = new();
            if (notificationHwnd.IsNull || !PInvoke.GetWindowRect(notificationHwnd, out trayRect))
            {
                return false;
            }

            int notificationAreaInPixels = trayRect.Width;
            float notificationAreaInDips = notificationAreaInPixels / scaleFactor;

            // Detect widget inline content and other system elements in the gap
            // between the task buttons and the notification area.
            (int effectiveLeft, int effectiveRight) = _widgetDetector.GetEffectiveContentBounds(
                maxRightInPixels, trayRect.left);

            float effectiveLeftDips = effectiveLeft / scaleFactor;
            TaskbarButtons.Width = new GridLength(effectiveLeftDips);

            double available = this.Bounds.Width;

            // Right reservation: from the effective right boundary to the taskbar edge.
            // Includes both the tray icons and any right-side widget content.
            double rightReservationDips = Math.Max(notificationAreaInDips, available - (effectiveRight / scaleFactor));
            TrayIcons.Width = new GridLength(rightReservationDips);

            double taskbarReserverdInDips = WindowsLogo.Width.Value + effectiveLeftDips;
            double forContent = available - taskbarReserverdInDips - rightReservationDips;

            if (_lastContentSpace == forContent)
            {
                _bandsControl?.SetMaxAvailableWidth(forContent);
                return false;
            }

            if (forContent > 0)
            {
                ContentColumn.MaxWidth = Root.ActualWidth == 0 ? double.MaxValue : forContent;
                ContentColumn.Width = GridLength.Auto;
                _bandsControl?.SetMaxAvailableWidth(forContent);
            }
            else
            {
                ContentColumn.MaxWidth = 0;
                ContentColumn.Width = new GridLength(0);
                _bandsControl?.SetMaxAvailableWidth(0);
            }
            _lastContentSpace = forContent;
            return true;
        }

        private async Task ClipWindow(bool onlyIfButtonsChanged = false)
        {
            if (_isClosing || IsExplorerInteractionSuspended)
            {
                return;
            }

            bool taskbarChanged = UpdateTaskbarButtons();
            if (onlyIfButtonsChanged && !taskbarChanged)
            {
                return;
            }
            await Task.Delay(100);
            if (_isClosing || IsExplorerInteractionSuspended || _hwnd == HWND.Null)
            {
                return;
            }

            float scaleFactor = this.GetDpiForWindow() / 96.0f;
            int height48px = (int)(48 * scaleFactor);

            // Force the window height to 48px
            PInvoke.GetWindowRect(_hwnd, out RECT currentRect);
            if (currentRect.Height != height48px)
            {
                if (!TryGetTaskbarRect(out RECT taskbarRect))
                {
                    ScheduleExplorerRecovery(ExplorerRetryDelay);
                    return;
                }

                PInvoke.SetWindowPos(_hwnd,
                    HWND_TOPMOST,
                    taskbarRect.left,
                    taskbarRect.top,
                    taskbarRect.Width,
                    height48px,
                    SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            }

            FrameworkElement clipToElement = MainContent;
            if (clipToElement.ActualWidth <= 0)
            {
                return;
            }

            Windows.Foundation.Point position = clipToElement.TransformToVisual(this.Content).TransformPoint(new());
            RECT scaledBounds = new()
            {
                left = (int)(position.X * scaleFactor),
                top = 0, // Always start at the very top
                right = (int)((position.X + clipToElement.ActualWidth) * scaleFactor),
                bottom = height48px // Always 48px tall
            };

            Windows.Win32.Graphics.Gdi.HRGN hrgn= PInvoke.CreateRectRgn(scaledBounds.left,
                    scaledBounds.top, scaledBounds.right, scaledBounds.bottom);
            int applied = PInvoke.SetWindowRgn(_hwnd, hrgn, true);
            if (applied == 0)
            {
                PInvoke.DeleteObject(hrgn);
            }
        }

        private void PrepareForClose()
        {
            if (_isClosing)
            {
                return;
            }

            _isClosing = true;
            _updateTaskbarButtonsTimer.Stop();
            _updateLayoutDebouncer?.Stop();
            _explorerRecoveryTimer.Stop();
            _topmostWatchTimer.Stop();
            RestoreOriginalWindowProc();
        }

        // ---------- Reactive z-order management ----------

        [DllImport("user32.dll")]
        private static extern HWND WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern HWND GetAncestor(HWND hWnd, uint flag);

        [DllImport("user32.dll")]
        private static extern HWND GetWindow(HWND hwnd, uint uCmd);

        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// If our overlay rect is being painted over by another topmost window,
        /// slide ourselves above the offender within the topmost band. Verifies
        /// coverage via WindowFromPoint so we don't restack unnecessarily.
        /// </summary>
        private void RestoreTopmostIfCovered()
        {
            if (_isClosing || _hwnd == HWND.Null || !_overlayInitialized)
            {
                return;
            }

            // Self-check: if we somehow lost WS_EX_TOPMOST, re-apply the overlay
            // setup — that's the only safe way back into the topmost band.
            if (!IsTopmost(_hwnd))
            {
                MoveToTaskbar();
                return;
            }

            if (!PInvoke.GetWindowRect(_hwnd, out RECT r) || r.Width <= 0 || r.Height <= 0)
            {
                return;
            }

            HWND offender = FindOffender(r);
            if (offender == HWND.Null || !IsTopmost(offender))
            {
                return;
            }

            // Slide above the offender. Both the offender and the slot above it
            // must be topmost: HWND_TOP or a non-topmost sibling would strip our
            // WS_EX_TOPMOST and hide the app underneath Shell_TrayWnd. If no
            // topmost slot exists above the offender, skip — shell will
            // rebalance and we'll re-check next tick.
            HWND insertAfter = GetWindow(offender, GW_HWNDPREV);
            if (insertAfter == HWND.Null || !IsTopmost(insertAfter))
            {
                return;
            }

            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                | SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING
                | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
            PInvoke.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, flags);
        }

        private HWND FindOffender(RECT r)
        {
            int midY = r.top + r.Height / 2;
            POINT[] samples =
            {
                new() { X = r.left + 4, Y = midY },
                new() { X = r.right - 4, Y = midY },
                new() { X = r.left + r.Width / 2, Y = midY },
            };

            foreach (POINT pt in samples)
            {
                HWND atPoint = WindowFromPoint(pt);
                if (atPoint == HWND.Null)
                {
                    continue;
                }

                HWND root = GetAncestor(atPoint, GA_ROOT);
                if (root != _hwnd)
                {
                    return root;
                }
            }

            return HWND.Null;
        }

        private static bool IsTopmost(HWND hwnd)
        {
            if (hwnd == HWND.Null)
            {
                return false;
            }

            nint exStyle = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            return ((uint)exStyle & (uint)WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0;
        }

        private void RestoreOriginalWindowProc()
        {
            if (_wndProcRestored || _originalWndProcPtr == 0 || _hwnd == HWND.Null)
            {
                return;
            }

            PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_WNDPROC, _originalWndProcPtr);
            _wndProcRestored = true;
        }

        public void Receive(TaskbarRestartMessage message)
        {
            ScheduleLayoutRefresh(TimeSpan.FromSeconds(2));
        }

        public void Receive(QuitMessage message)
        {
            _isExplicitQuit = true;
            this.VisibilityChanged -= MainWindow_VisibilityChanged;
            this.MainContent.SizeChanged -= ItemsBar_SizeChangedAsync;

            // Stop the debouncer to prevent any pending calls
            PrepareForClose();

            DispatcherQueue.TryEnqueue(() => Close());
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            PrepareForClose();
            _tasklist.Dispose();
            _widgetDetector.Dispose();
            _trayIconService.Destroy();

            // We only get here on an explicit quit — WM_DESTROY is swallowed
            // for any other reason in CustomWndProc to avoid a destroy/recreate
            // flicker loop driven by XAML lifecycle events.
            Environment.Exit(0);
        }
    }

    public record QuitMessage();
    public record TaskbarRestartMessage();
}
