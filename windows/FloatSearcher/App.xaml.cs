using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FloatSearcher
{
    public partial class App : Application
    {
        public static AppSettings Settings { get; private set; }
        public static BubbleWindow Bubble { get; private set; }
        static Mutex _single;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 只允许运行一个
            _single = new Mutex(true, "FloatSearcher.SingleInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("划词助手已经在运行了，看看屏幕边上的悬浮图标。", "划词助手",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            Settings = AppSettings.Load();
            ApplyTheme(IsDarkMode());

            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                try { MessageBox.Show("出了点问题：" + ex.Exception.Message, "划词助手"); } catch { }
            };

            Bubble = new BubbleWindow();
            Bubble.Show();

            // 第一次用：先填接口
            if (string.IsNullOrWhiteSpace(Settings.ApiKey)) OpenSettings();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Settings?.Save();
            try { _single?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }

        static SettingsWindow _settingsWin;

        public static void OpenSettings()
        {
            if (_settingsWin != null)
            {
                _settingsWin.Activate();
                return;
            }
            _settingsWin = new SettingsWindow();
            _settingsWin.Closed += (s, e) => _settingsWin = null;
            _settingsWin.Show();
            _settingsWin.Activate();
        }

        static bool IsDarkMode()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var v = k?.GetValue("AppsUseLightTheme");
                return v is int i && i == 0;
            }
            catch { return false; }
        }

        /// <summary>和安卓版一致的玉石绿配色，跟随 Windows 深浅色</summary>
        void ApplyTheme(bool dark)
        {
            void Set(string key, string hex)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                Resources[key] = b;
                Resources[key + "Color"] = c;
            }
            if (dark)
            {
                Set("Bg", "#0F1311"); Set("Surface", "#1A201D"); Set("Surface2", "#252D29");
                Set("Quote", "#1F2924"); Set("Ink", "#E5ECE8"); Set("InkSoft", "#98A8A0");
                Set("Line", "#2F3934"); Set("Accent", "#5FCFAF"); Set("AccentInk", "#0A1914");
                Set("AccentSoft", "#335FCFAF"); Set("Danger", "#F08A7E"); Set("Hover", "#2A332F");
            }
            else
            {
                Set("Bg", "#F2F4F1"); Set("Surface", "#FFFFFF"); Set("Surface2", "#E8EDE9");
                Set("Quote", "#EDF3EF"); Set("Ink", "#17201C"); Set("InkSoft", "#5B6A63");
                Set("Line", "#D5DDD8"); Set("Accent", "#1E7A62"); Set("AccentInk", "#FFFFFF");
                Set("AccentSoft", "#331E7A62"); Set("Danger", "#B23A2E"); Set("Hover", "#E1E8E3");
            }
        }

        public static Brush Brush(string key) => (Brush)Current.Resources[key];
    }
}
