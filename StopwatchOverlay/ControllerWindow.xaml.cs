using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;

namespace StopwatchOverlay
{
    public partial class ControllerWindow : Window
    {
        // Win32 API for global hotkeys
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_START_STOP = 1;
        private const int HOTKEY_RESET = 2;
        private const int HOTKEY_TOGGLE_OVERLAY = 3;
        private const int HOTKEY_LAP = 4;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_WIN = 0x0008;
        private const uint VK_F5 = 0x74;
        private const uint VK_F6 = 0x75;
        private const uint VK_F7 = 0x76;
        private const uint VK_F8 = 0x77;

        private readonly Stopwatch _stopwatch = new();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _blinkTimer;
        private readonly List<OverlayWindow> _overlayWindows = new();
        private readonly List<LightRingWindow> _lightRingWindows = new();
        private Dictionary<string, OverlayPosition> _overlayPositions = new();
        private bool _isRunning = false;
        private Screen? _selectedScreen;
        
        // Mode: 0=Stopwatch, 1=Clock, 2=Countdown, 3=Timecode
        private int _currentMode = 0;
        private TimeSpan _countdownDuration = TimeSpan.FromMinutes(5);
        private TimeSpan _countdownRemaining;
        private bool _countdownStarted;
        private bool _countdownCompleted;
        private bool _countdownAlertVisible;
        private bool _colonVisible = true;
        private int _timeFormat = 0; // 0=HH:MM:SS.t, 1=HH:MM:SS, 2=MM:SS.t, 3=MM:SS
        private int _frameRate = 30;
        private string _quickPreset1 = "1";
        private string _quickPreset2 = "5";
        private string _quickPreset3 = "10";
        private string _quickPreset4 = "30";
        private string _quickPreset5 = "60";
        private string _language = "en";
        private bool _languageReady;
        private bool _isLoadingSettings;
        private bool _canSaveSettings;

        private readonly ObservableCollection<string> _lapTimes = new();
        private int _lapCount = 0;
        private HwndSource? _hwndSource;
        private NotifyIcon? _trayIcon;
        private ToolStripMenuItem? _trayShowMenuItem;
        private ToolStripMenuItem? _trayExitMenuItem;
        private bool _isExiting;

        public ControllerWindow()
        {
            // Set dark theme before InitializeComponent so UI renders correctly from the start
            #pragma warning disable WPF0001
            System.Windows.Application.Current.ThemeMode = ThemeMode.Dark;
            #pragma warning restore WPF0001

            InitializeComponent();
            InitializeTrayIcon();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += Timer_Tick;

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _blinkTimer.Tick += BlinkTimer_Tick;

            LapListBox.ItemsSource = _lapTimes;

            PopulateScreens();
            LoadSettings();
            UpdateButtonStates();
            _timer.Start();
            _blinkTimer.Start();

            AutoStartCheckBox.Checked += SettingsControlChanged;
            AutoStartCheckBox.Unchecked += SettingsControlChanged;
            BlinkColonCheckBox.Checked += SettingsControlChanged;
            BlinkColonCheckBox.Unchecked += SettingsControlChanged;
            CountdownVisualAlertCheckBox.Checked += SettingsControlChanged;
            CountdownVisualAlertCheckBox.Unchecked += SettingsControlChanged;
            CountdownHours.TextChanged += SettingsTextChanged;
            CountdownMinutes.TextChanged += SettingsTextChanged;
            CountdownSeconds.TextChanged += SettingsTextChanged;

            LanguageSelector.SelectionChanged += LanguageSelector_SelectionChanged;

            // XAML controls raise change events while the window is being created.
            // Only allow persistence after the saved values have been restored.
            _canSaveSettings = true;
            _languageReady = true;
            ApplyLanguage();
        }

        private void InitializeTrayIcon()
        {
            var menu = new ContextMenuStrip();
            _trayShowMenuItem = new ToolStripMenuItem();
            _trayShowMenuItem.Click += (_, _) => ShowController();
            _trayExitMenuItem = new ToolStripMenuItem();
            _trayExitMenuItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(_trayShowMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_trayExitMenuItem);

            _trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => ShowController();
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized) Hide();
            };
            UpdateTrayText();
        }

        private void ShowController()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _isExiting = true;
            Close();
        }

        private void UpdateTrayText()
        {
            if (_trayIcon == null) return;

            bool chinese = _language == "zh";
            _trayIcon.Text = chinese ? "秒表悬浮窗" : "Stopwatch Overlay";
            if (_trayShowMenuItem != null) _trayShowMenuItem.Text = chinese ? "显示控制器" : "Show controller";
            if (_trayExitMenuItem != null) _trayExitMenuItem.Text = chinese ? "退出" : "Exit";
        }

        private void ThemeModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeModeSelector?.SelectedItem is not ComboBoxItem selected) return;
            var mode = selected.Content?.ToString() switch
            {
                "Light" => ThemeMode.Light,
                "System" => ThemeMode.System,
                _ => ThemeMode.Dark
            };
            #pragma warning disable WPF0001
            System.Windows.Application.Current.ThemeMode = mode;
            #pragma warning restore WPF0001
            SaveSettings();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            
            // Register global hotkeys
            var helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);
            _hwndSource?.AddHook(HwndHook);

            RegisterHotKey(helper.Handle, HOTKEY_START_STOP, MOD_WIN, VK_F5);
            RegisterHotKey(helper.Handle, HOTKEY_RESET, MOD_WIN, VK_F6);
            RegisterHotKey(helper.Handle, HOTKEY_TOGGLE_OVERLAY, MOD_WIN, VK_F7);
            RegisterHotKey(helper.Handle, HOTKEY_LAP, MOD_WIN, VK_F8);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                switch (hotkeyId)
                {
                    case HOTKEY_START_STOP:
                        StartStopButton_Click(this, new RoutedEventArgs());
                        handled = true;
                        break;
                    case HOTKEY_RESET:
                        ResetButton_Click(this, new RoutedEventArgs());
                        handled = true;
                        break;
                    case HOTKEY_TOGGLE_OVERLAY:
                        ToggleOverlayButton_Click(this, new RoutedEventArgs());
                        handled = true;
                        break;
                    case HOTKEY_LAP:
                        LapButton_Click(this, new RoutedEventArgs());
                        handled = true;
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void PopulateScreens()
        {
            ScreenSelector.Items.Clear();
            ScreenSelector.Items.Add(new ComboBoxItem { Content = "All Screens", Tag = null });
            
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string name = screen.Primary ? $"Screen {i + 1} (Primary)" : $"Screen {i + 1}";
                name += $" - {screen.Bounds.Width}x{screen.Bounds.Height}";
                ScreenSelector.Items.Add(new ComboBoxItem { Content = name, Tag = screen });
            }

            ScreenSelector.SelectedIndex = screens.Length > 1 ? 1 : 0;
            _selectedScreen = screens.Length > 0 ? screens[0] : null;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_currentMode == 2 && _isRunning) // Countdown mode
            {
                _countdownRemaining -= TimeSpan.FromMilliseconds(50);
                // Dispatcher ticks can be delayed, so notify once on the first tick at or below zero.
                if (_countdownRemaining <= TimeSpan.Zero && !_countdownCompleted)
                {
                    _countdownCompleted = true;
                    UpdateStatus("Time's up! (counting negative)", Brushes.Red);
                }
            }
            UpdateTimeDisplay();
        }

        private void BlinkTimer_Tick(object? sender, EventArgs e)
        {
            if (BlinkColonCheckBox?.IsChecked == true && _currentMode == 1) // Clock mode
            {
                _colonVisible = !_colonVisible;
                UpdateTimeDisplay();
            }
            else
            {
                _colonVisible = true;
            }

            if (_currentMode == 2 && _countdownCompleted && CountdownVisualAlertCheckBox?.IsChecked == true)
            {
                _countdownAlertVisible = !_countdownAlertVisible;
                SetCountdownAlert(_countdownAlertVisible);
            }
            else if (_countdownAlertVisible)
            {
                _countdownAlertVisible = false;
                SetCountdownAlert(false);
            }

            // Blink REC indicator
            if (_isRunning && ShowRecIndicatorCheckBox?.IsChecked == true)
            {
                RecIndicator.Visibility = RecIndicator.Visibility == Visibility.Visible 
                    ? Visibility.Hidden : Visibility.Visible;
                foreach (var overlay in _overlayWindows)
                {
                    overlay.SetRecIndicatorVisible(RecIndicator.Visibility == Visibility.Visible);
                }
            }
        }

        private void UpdateTimeDisplay()
        {
            string timeText = GetFormattedTime();
            TimeDisplay.Text = timeText;
            
            foreach (var overlay in _overlayWindows)
            {
                overlay.UpdateTime(timeText);
            }
        }

        private string GetFormattedTime()
        {
            string colon = _colonVisible ? ":" : " ";
            
            switch (_currentMode)
            {
                case 1: // Clock
                    var now = DateTime.Now;
                    return _timeFormat switch
                    {
                        0 => $"{now.Hour:D2}{colon}{now.Minute:D2}{colon}{now.Second:D2}.{now.Millisecond / 100:D1}",
                        1 => $"{now.Hour:D2}{colon}{now.Minute:D2}{colon}{now.Second:D2}",
                        2 => $"{now.Minute:D2}{colon}{now.Second:D2}.{now.Millisecond / 100:D1}",
                        3 => $"{now.Minute:D2}{colon}{now.Second:D2}",
                        _ => now.ToString("HH:mm:ss")
                    };

                case 2: // Countdown
                    var remaining = _countdownRemaining;
                    bool isNegative = remaining < TimeSpan.Zero;
                    var absRemaining = isNegative ? remaining.Negate() : remaining;
                    string sign = isNegative ? "-" : "";
                    return _timeFormat switch
                    {
                        0 => $"{sign}{(int)absRemaining.TotalHours:D2}:{absRemaining.Minutes:D2}:{absRemaining.Seconds:D2}.{absRemaining.Milliseconds / 100:D1}",
                        1 => $"{sign}{(int)absRemaining.TotalHours:D2}:{absRemaining.Minutes:D2}:{absRemaining.Seconds:D2}",
                        2 => $"{sign}{(int)absRemaining.TotalMinutes:D2}:{absRemaining.Seconds:D2}.{absRemaining.Milliseconds / 100:D1}",
                        3 => $"{sign}{(int)absRemaining.TotalMinutes:D2}:{absRemaining.Seconds:D2}",
                        _ => $"{sign}{absRemaining.Hours:D2}:{absRemaining.Minutes:D2}:{absRemaining.Seconds:D2}.{absRemaining.Milliseconds / 100:D1}"
                    };

                case 3: // Timecode (with frames)
                    var tc = _stopwatch.Elapsed;
                    int frames = (int)(tc.Milliseconds / (1000.0 / _frameRate));
                    return $"{tc.Hours:D2}:{tc.Minutes:D2}:{tc.Seconds:D2}:{frames:D2}";

                default: // Stopwatch
                    var elapsed = _stopwatch.Elapsed;
                    return _timeFormat switch
                    {
                        0 => $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds / 100:D1}",
                        1 => $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}",
                        2 => $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds / 100:D1}",
                        3 => $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}",
                        _ => elapsed.ToString(@"hh\:mm\:ss\.f")
                    };
            }
        }

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (CountdownPanel == null) return;

            if (StopwatchModeRadio?.IsChecked == true) _currentMode = 0;
            else if (ClockModeRadio?.IsChecked == true) _currentMode = 1;
            else if (CountdownModeRadio?.IsChecked == true) _currentMode = 2;
            else if (TimecodeModeRadio?.IsChecked == true) _currentMode = 3;

            CountdownPanel.Visibility = _currentMode == 2 ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtonStates();
            UpdateActionButtonLabels();
            UpdateTimeDisplay();

            string[] modeNames = { "Stopwatch", "Clock", "Countdown", "Timecode" };
            UpdateStatus($"{modeNames[_currentMode]} Mode", Brushes.DeepSkyBlue);
            SaveSettings();
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                // Pause. In countdown mode this preserves the remaining duration.
                _stopwatch.Stop();
                _isRunning = false;
                StartStopButton.Content = "▶ Start (Win+F5)";
                StartStopButton.Style = (Style)FindResource("StartButton");
                UpdateButtonStates();
                UpdateStatus("Paused", Brushes.Orange);

                RecIndicator.Visibility = Visibility.Collapsed;
                foreach (var overlay in _overlayWindows)
                {
                    overlay.SetRecIndicatorVisible(false);
                }
            }
            else
            {
                // Start a new countdown only once. Subsequent starts resume from the paused value.
                if (_currentMode == 2 && !_countdownStarted)
                {
                    int.TryParse(CountdownHours.Text, out int hours);
                    int.TryParse(CountdownMinutes.Text, out int mins);
                    int.TryParse(CountdownSeconds.Text, out int secs);
                    _countdownDuration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs);
                    _countdownRemaining = _countdownDuration;
                    _countdownStarted = true;
                    _countdownCompleted = false;
                    SetCountdownAlert(false);
                }
                
                _stopwatch.Start();
                _isRunning = true;
                StartStopButton.Content = "⏹ Stop (Win+F5)";
                StartStopButton.Style = (Style)FindResource("StopButton");
                UpdateButtonStates();
                UpdateStatus("Running", Brushes.LimeGreen);

                if (ShowRecIndicatorCheckBox?.IsChecked == true)
                {
                    RecIndicator.Visibility = Visibility.Visible;
                    foreach (var overlay in _overlayWindows)
                    {
                        overlay.SetRecIndicatorVisible(true);
                    }
                }

                // Starting the timer should make the overlay visible without requiring
                // a separate Show action (or Win+F7).
                if (_overlayWindows.Count == 0)
                {
                    ToggleOverlayButton_Click(sender, e);
                }
            }

            UpdateActionButtonLabels();
        }

        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_languageReady || LanguageSelector.SelectedItem is not ComboBoxItem item) return;

            _language = item.Tag?.ToString() ?? "en";
            ApplyLanguage();
            SaveSettings();
        }

        private void CountdownPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: string tag } ||
                !int.TryParse(GetQuickPresetText(tag), out int minutes) || minutes < 0) return;

            CountdownHours.Text = "0";
            CountdownMinutes.Text = minutes.ToString();
            CountdownSeconds.Text = "00";
            SaveSettings();
        }

        private void QuickPreset_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: string tag }) return;

            var editor = new QuickPresetEditorWindow(GetQuickPresetText(tag)) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                SetQuickPresetText(tag, editor.Minutes.ToString());
                UpdateQuickPresetButtons();
                SaveSettings();
            }

            e.Handled = true;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _stopwatch.Reset();
            _isRunning = false;
            StartStopButton.Content = "▶ Start (Win+F5)";
            StartStopButton.Style = (Style)FindResource("StartButton");
            
            if (_currentMode == 2)
            {
                int.TryParse(CountdownHours.Text, out int hours);
                int.TryParse(CountdownMinutes.Text, out int mins);
                int.TryParse(CountdownSeconds.Text, out int secs);
                _countdownDuration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs);
                _countdownRemaining = _countdownDuration;
                _countdownStarted = false;
                _countdownCompleted = false;
                SetCountdownAlert(false);
            }
            
            _lapTimes.Clear();
            _lapCount = 0;
            LapPlaceholder.Visibility = Visibility.Visible;
            
            UpdateTimeDisplay();
            UpdateButtonStates();
            UpdateStatus("Reset", Brushes.Gray);

            RecIndicator.Visibility = Visibility.Collapsed;
            foreach (var overlay in _overlayWindows)
            {
                overlay.SetRecIndicatorVisible(false);
            }

            UpdateActionButtonLabels();
        }

        private void LapButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMode == 1) return; // No lap for clock mode

            _lapCount++;
            string lapTime = $"Lap {_lapCount}: {GetFormattedTime()}";
            _lapTimes.Insert(0, lapTime);
            LapPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void ToggleOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_overlayWindows.Count > 0)
            {
                // Hide overlays
                foreach (var overlay in _overlayWindows) overlay.Close();
                _overlayWindows.Clear();
                ToggleOverlayButton.Content = "👁 Show (Win+F7)";
                UpdateStatus(_isRunning ? "Running (Overlay Hidden)" : "Overlay Hidden", 
                    _isRunning ? Brushes.LimeGreen : Brushes.Gray);
            }
            else
            {
                // Show overlays
                var selectedItem = ScreenSelector.SelectedItem as ComboBoxItem;
                
                if (selectedItem?.Tag == null) // "All Screens"
                {
                    foreach (var screen in Screen.AllScreens)
                    {
                        CreateOverlayForScreen(screen);
                    }
                }
                else if (selectedItem.Tag is Screen screen)
                {
                    CreateOverlayForScreen(screen);
                }

                if (AutoStartCheckBox?.IsChecked == true && !_isRunning && _currentMode != 1)
                {
                    StartStopButton_Click(sender, e);
                }

                ToggleOverlayButton.Content = "🙈 Hide (Win+F7)";
                UpdateStatus($"Overlay visible on {_overlayWindows.Count} screen(s)", Brushes.DeepSkyBlue);
            }

            UpdateActionButtonLabels();
        }

        private void CreateOverlayForScreen(Screen screen)
        {
            var overlay = new OverlayWindow();
            ApplyOverlaySettings(overlay);
            if (_overlayPositions.TryGetValue(screen.DeviceName, out var savedPosition))
            {
                overlay.Left = savedPosition.Left;
                overlay.Top = savedPosition.Top;
            }
            else
            {
                PositionOverlay(overlay, screen);
            }
            overlay.Show();
            overlay.UpdateTime(GetFormattedTime());

            overlay.PositionChangedByUser += (_, _) =>
            {
                _overlayPositions[screen.DeviceName] = new OverlayPosition
                {
                    Left = overlay.Left,
                    Top = overlay.Top
                };
                SaveSettings();
            };
            overlay.DoubleClicked += (_, _) =>
            {
                if (_currentMode == 2 && _isRunning)
                {
                    ShowController();
                }
            };
            
            if (ClickThroughCheckBox?.IsChecked == true)
            {
                overlay.SetClickThrough(true);
            }
            
            if (_isRunning && ShowRecIndicatorCheckBox?.IsChecked == true)
            {
                overlay.SetRecIndicatorVisible(true);
            }
            
            _overlayWindows.Add(overlay);
        }

        private void ScreenSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScreenSelector.SelectedItem is ComboBoxItem item && item.Tag is Screen screen)
            {
                _selectedScreen = screen;
            }
            
            // Reposition if overlays are showing
            if (_overlayWindows.Count > 0)
            {
                // Close and reopen to reposition
                foreach (var overlay in _overlayWindows) overlay.Close();
                _overlayWindows.Clear();
                ToggleOverlayButton_Click(sender, new RoutedEventArgs());
            }
            SaveSettings();
        }

        private void PositionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selecting a preset position intentionally replaces any dragged position.
            if (!_isLoadingSettings) _overlayPositions.Clear();

            // Reposition all overlays
            if (_overlayWindows.Count > 0)
            {
                foreach (var overlay in _overlayWindows) overlay.Close();
                _overlayWindows.Clear();
                ToggleOverlayButton_Click(sender, new RoutedEventArgs());
            }
            SaveSettings();
        }

        private void PositionOverlay(OverlayWindow overlay, Screen screen)
        {
            var bounds = screen.Bounds;
            var position = (PositionSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Top Center";

            overlay.UpdateLayout();
            var dpiScale = GetDpiScaleForScreen(screen);
            
            double overlayWidth = overlay.ActualWidth > 0 ? overlay.ActualWidth : 300;
            double overlayHeight = overlay.ActualHeight > 0 ? overlay.ActualHeight : 80;

            double screenLeft = bounds.Left / dpiScale;
            double screenTop = bounds.Top / dpiScale;
            double screenWidth = bounds.Width / dpiScale;
            double screenHeight = bounds.Height / dpiScale;
            double screenRight = screenLeft + screenWidth;
            double screenBottom = screenTop + screenHeight;

            int margin = 10;

            (overlay.Left, overlay.Top) = position switch
            {
                "Top Left" => (screenLeft + margin, screenTop + margin),
                "Top Center" => (screenLeft + (screenWidth - overlayWidth) / 2, screenTop + margin),
                "Top Right" => (screenRight - overlayWidth - margin, screenTop + margin),
                "Bottom Left" => (screenLeft + margin, screenBottom - overlayHeight - margin),
                "Bottom Center" => (screenLeft + (screenWidth - overlayWidth) / 2, screenBottom - overlayHeight - margin),
                "Bottom Right" => (screenRight - overlayWidth - margin, screenBottom - overlayHeight - margin),
                _ => (screenLeft + (screenWidth - overlayWidth) / 2, screenTop + margin)
            };
        }

        private double GetDpiScaleForScreen(Screen screen)
        {
            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    return source.CompositionTarget.TransformToDevice.M11;
                }
            }
            catch { }
            return 1.0;
        }

        private void AppearanceChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyAllOverlaySettings();
            SaveSettings();
        }

        private void AppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextSizeLabel != null) TextSizeLabel.Text = ((int)TextSizeSlider.Value).ToString();
            if (BorderWidthLabel != null) BorderWidthLabel.Text = ((int)BorderWidthSlider.Value).ToString();
            if (BackgroundOpacityLabel != null) BackgroundOpacityLabel.Text = $"{(int)BackgroundOpacitySlider.Value}%";
            
            ApplyAllOverlaySettings();
            SaveSettings();
        }

        private void TimeFormatSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _timeFormat = TimeFormatSelector?.SelectedIndex ?? 0;
            UpdateTimeDisplay();
            SaveSettings();
        }

        private void ShowRecIndicatorCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool show = ShowRecIndicatorCheckBox?.IsChecked == true && _isRunning;
            RecIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            foreach (var overlay in _overlayWindows)
            {
                overlay.SetRecIndicatorVisible(show);
            }
            SaveSettings();
        }

        private void ClickThroughCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool clickThrough = ClickThroughCheckBox?.IsChecked == true;
            foreach (var overlay in _overlayWindows)
            {
                overlay.SetClickThrough(clickThrough);
            }
            SaveSettings();
        }

        #region Light Ring

        private void LightRingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (LightRingCheckBox?.IsChecked == true)
            {
                ShowLightRing();
            }
            else
            {
                HideLightRing();
            }
            SaveSettings();
        }

        private void LightRingSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LightRingBrightnessLabel != null)
                LightRingBrightnessLabel.Text = $"{(int)LightRingBrightnessSlider.Value}%";
            if (LightRingWidthLabel != null)
                LightRingWidthLabel.Text = $"{(int)LightRingWidthSlider.Value}px";
            
            UpdateLightRingSettings();
            SaveSettings();
        }

        private void LightRingSliderChanged(object sender, RoutedEventArgs e)
        {
            // Overload for checkbox events
            UpdateLightRingSettings();
            SaveSettings();
        }

        private void ShowLightRing()
        {
            HideLightRing();

            var selectedItem = ScreenSelector.SelectedItem as ComboBoxItem;
            
            if (selectedItem?.Tag == null) // "All Screens"
            {
                foreach (var screen in Screen.AllScreens)
                {
                    CreateLightRingForScreen(screen);
                }
            }
            else if (selectedItem.Tag is Screen screen)
            {
                CreateLightRingForScreen(screen);
            }
        }

        private void CreateLightRingForScreen(Screen screen)
        {
            var lightRing = new LightRingWindow();
            var brightness = (LightRingBrightnessSlider?.Value ?? 100) / 100.0;
            var width = (int)(LightRingWidthSlider?.Value ?? 20);
            var hideFromCapture = LightRingHideFromCaptureCheckBox?.IsChecked == true;
            
            lightRing.Show();
            lightRing.PositionOnScreen(screen);
            lightRing.ApplySettings(brightness, width, hideFromCapture);
            
            _lightRingWindows.Add(lightRing);
        }

        private void HideLightRing()
        {
            foreach (var lightRing in _lightRingWindows)
            {
                lightRing.Close();
            }
            _lightRingWindows.Clear();
        }

        private void UpdateLightRingSettings()
        {
            if (_lightRingWindows.Count == 0) return;

            var brightness = (LightRingBrightnessSlider?.Value ?? 100) / 100.0;
            var width = (int)(LightRingWidthSlider?.Value ?? 20);
            var hideFromCapture = LightRingHideFromCaptureCheckBox?.IsChecked == true;

            foreach (var lightRing in _lightRingWindows)
            {
                lightRing.ApplySettings(brightness, width, hideFromCapture);
            }
        }

        #endregion

        private void ApplyAllOverlaySettings()
        {
            foreach (var overlay in _overlayWindows)
            {
                ApplyOverlaySettings(overlay);
            }
        }

        private void ApplyOverlaySettings(OverlayWindow overlay)
        {
            if (TextColorSelector == null) return;

            var textColor = GetColorFromSelection(TextColorSelector);
            var borderColor = GetColorFromSelection(BorderColorSelector);
            var fontSize = (int)(TextSizeSlider?.Value ?? 48);
            var borderWidth = (int)(BorderWidthSlider?.Value ?? 2);
            var fontFamily = (FontSelector?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Consolas";
            var bgOpacity = (BackgroundOpacitySlider?.Value ?? 50) / 100.0;

            overlay.ApplySettings(textColor, borderColor, fontSize, borderWidth, fontFamily, bgOpacity);
        }

        private Color GetColorFromSelection(System.Windows.Controls.ComboBox comboBox)
        {
            var selection = (comboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "White";
            return selection switch
            {
                "White" => Colors.White,
                "Yellow" => Colors.Yellow,
                "Cyan" => Colors.Cyan,
                "Lime" => Colors.Lime,
                "Orange" => Colors.Orange,
                "Red" => Colors.Red,
                "Magenta" => Colors.Magenta,
                "Black" => Colors.Black,
                "Dark Gray" => Colors.DarkGray,
                "Blue" => Colors.Blue,
                _ => Colors.White
            };
        }

        private void UpdateButtonStates()
        {
            bool isClockMode = _currentMode == 1;
            StartStopButton.IsEnabled = !isClockMode;
            ResetButton.IsEnabled = !isClockMode;
            LapButton.IsEnabled = !isClockMode;
        }

        private void UpdateStatus(string text, Brush color)
        {
            StatusText.Text = Translate(text);
            StatusIndicator.Fill = color;
        }

        private void SetCountdownAlert(bool isVisible)
        {
            _countdownAlertVisible = isVisible;
            foreach (var overlay in _overlayWindows)
            {
                overlay.SetCountdownAlert(isVisible);
            }
        }

        private void UpdateActionButtonLabels()
        {
            var startStopLabel = _currentMode == 2
                ? _isRunning ? "⏸ Pause (Win+F5)" : _countdownStarted ? "▶ Resume (Win+F5)" : "▶ Start (Win+F5)"
                : _isRunning ? "⏹ Stop (Win+F5)" : "▶ Start (Win+F5)";
            StartStopButton.Content = Translate(startStopLabel);
            StartStopButton.Style = (Style)FindResource(_currentMode == 2 && _isRunning ? "PauseButton" : _isRunning ? "StopButton" : "StartButton");
            ResetButton.Content = Translate("↻ Reset (Win+F6)");
            ToggleOverlayButton.Content = Translate(_overlayWindows.Count > 0 ? "🙈 Hide (Win+F7)" : "👁 Show (Win+F7)");
            LapButton.Content = Translate("⚑ Lap (Win+F8)");
        }

        private void SettingsControlChanged(object sender, RoutedEventArgs e) => SaveSettings();

        private void SettingsTextChanged(object sender, TextChangedEventArgs e) => SaveSettings();

        private string GetQuickPresetText(string tag) => tag switch
        {
            "1" => _quickPreset1,
            "2" => _quickPreset2,
            "3" => _quickPreset3,
            "4" => _quickPreset4,
            "5" => _quickPreset5,
            _ => string.Empty
        };

        private void SetQuickPresetText(string tag, string minutes)
        {
            switch (tag)
            {
                case "1": _quickPreset1 = minutes; break;
                case "2": _quickPreset2 = minutes; break;
                case "3": _quickPreset3 = minutes; break;
                case "4": _quickPreset4 = minutes; break;
                case "5": _quickPreset5 = minutes; break;
            }
        }

        private void UpdateQuickPresetButtons()
        {
            QuickPreset1Button.Content = $"{_quickPreset1} {Translate("min")}";
            QuickPreset2Button.Content = $"{_quickPreset2} {Translate("min")}";
            QuickPreset3Button.Content = $"{_quickPreset3} {Translate("min")}";
            QuickPreset4Button.Content = $"{_quickPreset4} {Translate("min")}";
            QuickPreset5Button.Content = $"{_quickPreset5} {Translate("min")}";
        }

        private void LoadSettings()
        {
            _isLoadingSettings = true;
            try
            {
                var settings = UserSettingsStore.Load();
                _language = settings.Language is "zh" ? "zh" : "en";
                LanguageSelector.SelectedIndex = _language == "zh" ? 1 : 0;
                _overlayPositions = settings.OverlayPositions ?? new Dictionary<string, OverlayPosition>();
                SelectComboBoxItem(ThemeModeSelector, settings.Theme);
                ScreenSelector.SelectedIndex = Math.Clamp(settings.ScreenIndex, 0, ScreenSelector.Items.Count - 1);
                SelectComboBoxItem(PositionSelector, settings.Position);
                SelectComboBoxItem(TextColorSelector, settings.TextColor);
                SelectComboBoxItem(BorderColorSelector, settings.BorderColor);
                SelectComboBoxItem(FontSelector, settings.Font);
                TimeFormatSelector.SelectedIndex = Math.Clamp(settings.TimeFormat, 0, TimeFormatSelector.Items.Count - 1);
                TextSizeSlider.Value = Math.Clamp(settings.TextSize, TextSizeSlider.Minimum, TextSizeSlider.Maximum);
                BorderWidthSlider.Value = Math.Clamp(settings.BorderWidth, BorderWidthSlider.Minimum, BorderWidthSlider.Maximum);
                BackgroundOpacitySlider.Value = Math.Clamp(settings.BackgroundOpacity, BackgroundOpacitySlider.Minimum, BackgroundOpacitySlider.Maximum);
                AutoStartCheckBox.IsChecked = settings.AutoStart;
                ShowRecIndicatorCheckBox.IsChecked = settings.ShowRecIndicator;
                ClickThroughCheckBox.IsChecked = settings.ClickThrough;
                BlinkColonCheckBox.IsChecked = settings.BlinkColon;
                CountdownVisualAlertCheckBox.IsChecked = settings.FlashOverlayOnCountdownComplete;
                CountdownHours.Text = settings.CountdownHours;
                CountdownMinutes.Text = settings.CountdownMinutes;
                CountdownSeconds.Text = settings.CountdownSeconds;
                _quickPreset1 = settings.QuickPreset1;
                _quickPreset2 = settings.QuickPreset2;
                _quickPreset3 = settings.QuickPreset3;
                _quickPreset4 = settings.QuickPreset4;
                _quickPreset5 = settings.QuickPreset5;
                UpdateQuickPresetButtons();
                LightRingBrightnessSlider.Value = Math.Clamp(settings.LightRingBrightness, LightRingBrightnessSlider.Minimum, LightRingBrightnessSlider.Maximum);
                LightRingWidthSlider.Value = Math.Clamp(settings.LightRingWidth, LightRingWidthSlider.Minimum, LightRingWidthSlider.Maximum);
                LightRingHideFromCaptureCheckBox.IsChecked = settings.LightRingHideFromCapture;
                LightRingCheckBox.IsChecked = settings.LightRingEnabled;

                (settings.Mode switch
                {
                    1 => ClockModeRadio,
                    2 => CountdownModeRadio,
                    3 => TimecodeModeRadio,
                    _ => StopwatchModeRadio
                }).IsChecked = true;

                _timeFormat = TimeFormatSelector.SelectedIndex;
                AppearanceSliderChanged(this, null!);
                LightRingSliderChanged(this, new RoutedEventArgs());
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private static void SelectComboBoxItem(System.Windows.Controls.ComboBox comboBox, string content)
        {
            var item = comboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Content?.ToString(), content, StringComparison.Ordinal));
            if (item != null) comboBox.SelectedItem = item;
        }

        private void SaveSettings()
        {
            if (_isLoadingSettings || !_canSaveSettings) return;

            UserSettingsStore.Save(new UserSettings
            {
                Mode = _currentMode,
                ScreenIndex = ScreenSelector.SelectedIndex,
                Theme = GetComboBoxContent(ThemeModeSelector, "Dark"),
                Language = _language,
                Position = GetComboBoxContent(PositionSelector, "Top Center"),
                TextColor = GetComboBoxContent(TextColorSelector, "White"),
                BorderColor = GetComboBoxContent(BorderColorSelector, "Black"),
                Font = GetComboBoxContent(FontSelector, "Consolas"),
                TimeFormat = TimeFormatSelector.SelectedIndex,
                TextSize = TextSizeSlider.Value,
                BorderWidth = BorderWidthSlider.Value,
                BackgroundOpacity = BackgroundOpacitySlider.Value,
                AutoStart = AutoStartCheckBox.IsChecked == true,
                ShowRecIndicator = ShowRecIndicatorCheckBox.IsChecked == true,
                ClickThrough = ClickThroughCheckBox.IsChecked == true,
                BlinkColon = BlinkColonCheckBox.IsChecked == true,
                FlashOverlayOnCountdownComplete = CountdownVisualAlertCheckBox.IsChecked == true,
                CountdownHours = CountdownHours.Text,
                CountdownMinutes = CountdownMinutes.Text,
                CountdownSeconds = CountdownSeconds.Text,
                QuickPreset1 = _quickPreset1,
                QuickPreset2 = _quickPreset2,
                QuickPreset3 = _quickPreset3,
                QuickPreset4 = _quickPreset4,
                QuickPreset5 = _quickPreset5,
                LightRingEnabled = LightRingCheckBox.IsChecked == true,
                LightRingBrightness = LightRingBrightnessSlider.Value,
                LightRingWidth = LightRingWidthSlider.Value,
                LightRingHideFromCapture = LightRingHideFromCaptureCheckBox.IsChecked == true,
                OverlayPositions = new Dictionary<string, OverlayPosition>(_overlayPositions)
            });
        }

        private static string GetComboBoxContent(System.Windows.Controls.ComboBox comboBox, string defaultValue) =>
            (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? defaultValue;

        private void ApplyLanguage()
        {
            Title = Translate("Stopwatch Controller");
            TranslateElement(this);
            UpdateQuickPresetButtons();
            UpdateActionButtonLabels();
            UpdateTrayText();
        }

        private void TranslateElement(object element)
        {
            if (element is ComboBoxItem) return; // These are configuration values, not UI labels.
            if (element is TextBlock textBlock) textBlock.Text = Translate(textBlock.Text);
            if (element is ContentControl contentControl && contentControl.Content is string content)
                contentControl.Content = Translate(content);

            if (element is not DependencyObject dependencyObject) return;
            foreach (var child in LogicalTreeHelper.GetChildren(dependencyObject))
                TranslateElement(child);
        }

        private string Translate(string text)
        {
            var translations = new Dictionary<string, string>
            {
                ["Stopwatch Controller"] = "秒表控制器",
                ["Stopwatch Overlay"] = "秒表悬浮窗",
                ["Stopwatch"] = "秒表",
                ["Clock"] = "时钟",
                ["Countdown"] = "倒计时",
                ["Timecode"] = "时间码",
                ["Duration:"] = "时长：",
                ["h"] = "小时",
                ["min"] = "分钟",
                ["sec"] = "秒",
                ["▶ Start (Win+F5)"] = "▶ 开始 (Win+F5)",
                ["⏹ Stop (Win+F5)"] = "⏹ 停止 (Win+F5)",
                ["⏸ Pause (Win+F5)"] = "⏸ 暂停 (Win+F5)",
                ["▶ Resume (Win+F5)"] = "▶ 继续 (Win+F5)",
                ["↻ Reset (Win+F6)"] = "↻ 重置 (Win+F6)",
                ["👁 Show (Win+F7)"] = "👁 显示 (Win+F7)",
                ["🙈 Hide (Win+F7)"] = "🙈 隐藏 (Win+F7)",
                ["⚑ Lap (Win+F8)"] = "⚑ 计次 (Win+F8)",
                ["Ready"] = "就绪",
                ["Running"] = "运行中",
                ["Paused"] = "已暂停",
                ["Reset"] = "已重置",
                ["Overlay Hidden"] = "悬浮窗已隐藏",
                ["Auto-start on show"] = "显示时自动开始",
                ["REC indicator"] = "录制指示器",
                ["Click-through"] = "鼠标穿透",
                ["Blink colon"] = "闪烁冒号",
                ["Flash overlay when countdown ends"] = "倒计时结束时闪动悬浮窗",
                ["Settings"] = "设置",
                ["Lap Times"] = "计次记录",
                ["Screen & Position"] = "屏幕与位置",
                ["Appearance"] = "外观",
                ["Light Ring (Screen Border)"] = "光环（屏幕边框）",
                ["Theme:"] = "主题：",
                ["Screen:"] = "屏幕：",
                ["Position:"] = "位置：",
                ["Language:"] = "语言：",
                ["Text:"] = "文字：",
                ["Border:"] = "描边：",
                ["Font:"] = "字体：",
                ["Format:"] = "格式：",
                ["Size:"] = "大小：",
                ["Outline:"] = "轮廓：",
                ["BG:"] = "背景：",
                ["Enable"] = "启用",
                ["Brightness:"] = "亮度：",
                ["Width:"] = "宽度：",
                ["Hide from screen capture"] = "录屏时隐藏",
                ["Right-click to edit"] = "右键点击编辑",
                ["Press Win+F8 or click Lap to record split times"] = "按 Win+F8 或点击计次记录分段时间",
                ["Win+F5 Start/Stop  Win+F6 Reset  Win+F7 Overlay  Win+F8 Lap"] = "Win+F5 开始/暂停  Win+F6 重置  Win+F7 悬浮窗  Win+F8 计次"
            };

            if (_language == "zh") return translations.TryGetValue(text, out var chinese) ? chinese : text;
            var english = translations.FirstOrDefault(pair => pair.Value == text).Key;
            return english ?? text;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                SaveSettings();
                e.Cancel = true;
                Hide();
                return;
            }

            SaveSettings();
            // Unregister hotkeys
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_START_STOP);
            UnregisterHotKey(helper.Handle, HOTKEY_RESET);
            UnregisterHotKey(helper.Handle, HOTKEY_TOGGLE_OVERLAY);
            UnregisterHotKey(helper.Handle, HOTKEY_LAP);

            foreach (var overlay in _overlayWindows) overlay.Close();
            foreach (var lightRing in _lightRingWindows) lightRing.Close();
            _timer.Stop();
            _blinkTimer.Stop();
            _trayIcon?.Dispose();
        }
    }
}
