using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FloatSearcher
{
    /// <summary>
    /// 桌面悬浮图标。
    ///  - 鼠标移上去：图标放大、外圈光环扩散，稍停一下展开扇形选项
    ///  - 扇形三个选项：中→英 / AI 对话 / 剪贴板，点一下切换（会记住）
    ///  - 单击图标：在旁边横向弹出输入窗
    ///  - 按住拖动：移动；右键：设置、退出
    /// </summary>
    public partial class BubbleWindow : Window
    {
        const double Half = 150;       // 窗口一半宽高：图标中心
        const double FanRadius = 98;   // 选项离图标中心的距离
        const double SpreadDeg = 52;   // 相邻选项之间的角度

        class FanItem
        {
            public Mode Mode;
            public Border View;
            public TextBlock Text;
            public ScaleTransform Scale;
            public TranslateTransform Move;
        }

        readonly List<FanItem> _items = new List<FanItem>();
        readonly DispatcherTimer _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        readonly DispatcherTimer _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        bool _fanOpen;
        bool _pressed;
        Point _downAt;

        public BubbleWindow()
        {
            InitializeComponent();
            BuildFan();

            _openTimer.Tick += (s, e) => { _openTimer.Stop(); OpenFan(); };
            _closeTimer.Tick += (s, e) => { _closeTimer.Stop(); CloseFan(); };

            BubbleHost.MouseEnter += (s, e) => { HoverOn(); if (!_fanOpen) _openTimer.Start(); };
            BubbleHost.MouseLeave += (s, e) => { HoverOff(); _openTimer.Stop(); };
            MouseEnter += (s, e) => _closeTimer.Stop();
            MouseLeave += (s, e) => { _openTimer.Stop(); _closeTimer.Start(); };

            BubbleHost.MouseLeftButtonDown += OnDown;
            BubbleHost.MouseMove += OnMove;
            BubbleHost.MouseLeftButtonUp += OnUp;
            BubbleHost.MouseRightButtonUp += (s, e) => { e.Handled = true; ShowMenu(); };

            // 先按上次的位置摆好，显示后再按所在屏幕校正
            var st = App.Settings;
            var wa0 = SystemParameters.WorkArea;
            Left = (double.IsNaN(st.BubbleX) ? wa0.Right - 44 : st.BubbleX) - Half;
            Top = (double.IsNaN(st.BubbleY) ? wa0.Top + wa0.Height * 0.4 : st.BubbleY) - Half;

            SourceInitialized += (s, e) => Native.HideFromAltTab(this);
            Loaded += (s, e) => { PlaceInitially(); RefreshMode(); };
        }

        // ================= 位置 =================

        Point Center => new Point(Left + Half, Top + Half);

        void PlaceInitially()
        {
            var s = App.Settings;
            Point c;
            if (double.IsNaN(s.BubbleX) || double.IsNaN(s.BubbleY))
            {
                var wa = SystemParameters.WorkArea;
                c = new Point(wa.Right - 44, wa.Top + wa.Height * 0.4);
            }
            else c = new Point(s.BubbleX, s.BubbleY);
            MoveCenterTo(c);
        }

        /// <summary>把图标中心放到 c，并保证图标完整在所在屏幕的工作区里</summary>
        void MoveCenterTo(Point c)
        {
            var wa = Native.WorkAreaAt(this, c);
            double m = 30;
            double x = Math.Max(wa.Left + m, Math.Min(wa.Right - m, c.X));
            double y = Math.Max(wa.Top + m, Math.Min(wa.Bottom - m, c.Y));
            Left = x - Half;
            Top = y - Half;
        }

        public Rect BubbleRect => new Rect(Left + Half - 28, Top + Half - 28, 56, 56);

        // ================= 点击 / 拖动 =================

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = true;
            _downAt = e.GetPosition(this);
            BubbleHost.CaptureMouse();
            e.Handled = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _downAt.X) < 4 && Math.Abs(p.Y - _downAt.Y) < 4) return;

            // 开始拖动
            _pressed = false;
            BubbleHost.ReleaseMouseCapture();
            _openTimer.Stop();
            CloseFan();
            try { DragMove(); } catch { }
            MoveCenterTo(Center);
            App.Settings.BubbleX = Center.X;
            App.Settings.BubbleY = Center.Y;
            App.Settings.Save();
            InputBar.Current?.Reposition();
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;
            BubbleHost.ReleaseMouseCapture();
            e.Handled = true;
            _openTimer.Stop();
            CloseFan();
            InputBar.Toggle(this);
        }

        // ================= 悬停动画 =================

        void HoverOn()
        {
            BubbleHost.Opacity = 1;
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.14, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.14, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });

            // 光圈：从图标边缘向外扩散并淡出，循环
            var dur = TimeSpan.FromMilliseconds(1100);
            var grow = new DoubleAnimation(1.0, 1.55, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fade = new DoubleAnimation(0.75, 0.0, dur) { RepeatBehavior = RepeatBehavior.Forever };
            RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            Ring.BeginAnimation(OpacityProperty, fade);
        }

        void HoverOff()
        {
            var dur = TimeSpan.FromMilliseconds(160);
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur));
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur));
            RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Ring.BeginAnimation(OpacityProperty, null);
            Ring.Opacity = 0;
            BubbleHost.Opacity = App.Settings.BubbleOpacity;
        }

        /// <summary>选项切换后让图标「跳」一下，表示已切换</summary>
        void Bounce()
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)),
                new BackEase { EasingMode = EasingMode.EaseOut }));
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // ================= 扇形选项 =================

        static readonly (Mode mode, string label)[] Options =
        {
            (Mode.Translate, "中 → 英"),
            (Mode.Chat, "AI 对话"),
            (Mode.Clipboard, "剪贴板"),
        };

        public static string Glyph(Mode m) => m switch
        {
            Mode.Translate => "译",
            Mode.Chat => "AI",
            _ => "贴",
        };

        public static string ModeName(Mode m) => m switch
        {
            Mode.Translate => "中译英",
            Mode.Chat => "AI 对话",
            _ => "剪贴板",
        };

        void BuildFan()
        {
            foreach (var (mode, label) in Options)
            {
                var text = new TextBlock
                {
                    Text = label,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var scale = new ScaleTransform(1, 1);
                var move = new TranslateTransform(0, 0);
                var tg = new TransformGroup();
                tg.Children.Add(scale);
                tg.Children.Add(move);
                var view = new Border
                {
                    Width = 86,
                    Height = 34,
                    CornerRadius = new CornerRadius(17),
                    BorderThickness = new Thickness(1),
                    Child = text,
                    Cursor = Cursors.Hand,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = tg,
                    Visibility = Visibility.Collapsed,
                    Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.25 },
                    ToolTip = mode switch
                    {
                        Mode.Translate => "输入中文，回车出英文",
                        Mode.Chat => "和 AI 对话，回车发送",
                        _ => "复制后点图标，自动带上剪贴板内容",
                    },
                };
                var item = new FanItem { Mode = mode, View = view, Text = text, Scale = scale, Move = move };
                view.MouseEnter += (s, e) => { if (App.Settings.Mode != item.Mode) view.Background = App.Brush("Hover"); };
                view.MouseLeave += (s, e) => StyleItem(item);
                view.MouseLeftButtonUp += (s, e) => { e.Handled = true; Choose(item.Mode); };
                _items.Add(item);
                FanLayer.Children.Add(view);
            }
        }

        void StyleItem(FanItem it)
        {
            bool on = App.Settings.Mode == it.Mode;
            it.View.Background = App.Brush(on ? "Accent" : "Surface");
            it.View.BorderBrush = App.Brush(on ? "Accent" : "Line");
            it.Text.Foreground = App.Brush(on ? "AccentInk" : "Ink");
        }

        void Choose(Mode m)
        {
            App.Settings.Mode = m;
            App.Settings.Save();
            RefreshMode();
            CloseFan();
            Bounce();
            InputBar.Current?.SwitchMode(m);
        }

        public void RefreshMode()
        {
            GlyphText.Text = Glyph(App.Settings.Mode);
            GlyphText.FontSize = App.Settings.Mode == Mode.Chat ? 16 : 18;
            BubbleHost.ToolTip = "当前：" + ModeName(App.Settings.Mode) + "　（单击打开，悬停切换，右键设置）";
            if (!BubbleHost.IsMouseOver) BubbleHost.Opacity = App.Settings.BubbleOpacity;
            foreach (var it in _items) StyleItem(it);
        }

        /// <summary>扇形朝向屏幕中间展开（靠右边时往左，靠左边时往右）</summary>
        double FanDirection()
        {
            var c = Center;
            var wa = Native.WorkAreaAt(this, c);
            double dx = (wa.Left + wa.Width / 2) - c.X;
            double dy = (wa.Top + wa.Height / 2) - c.Y;
            // 偏向水平方向展开，更自然
            return Math.Atan2(dy * 0.6, dx);
        }

        void OpenFan()
        {
            if (_fanOpen || _pressed) return;
            _fanOpen = true;
            double dir = FanDirection();

            // 感应区：图标到选项之间的扇形
            double r = FanRadius + 52;
            double a0 = dir - (SpreadDeg + 34) * Math.PI / 180;
            double a1 = dir + (SpreadDeg + 34) * Math.PI / 180;
            var fig = new PathFigure { StartPoint = new Point(Half, Half), IsClosed = true };
            fig.Segments.Add(new LineSegment(new Point(Half + r * Math.Cos(a0), Half + r * Math.Sin(a0)), false));
            fig.Segments.Add(new ArcSegment(new Point(Half + r * Math.Cos(a1), Half + r * Math.Sin(a1)),
                new Size(r, r), 0, false, SweepDirection.Clockwise, false));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            FanHit.Data = geo;
            FanHit.Visibility = Visibility.Visible;

            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                StyleItem(it);
                double ang = dir + (i - 1) * SpreadDeg * Math.PI / 180;
                double cx = Half + FanRadius * Math.Cos(ang);
                double cy = Half + FanRadius * Math.Sin(ang);
                Canvas.SetLeft(it.View, cx - it.View.Width / 2);
                Canvas.SetTop(it.View, cy - it.View.Height / 2);
                it.View.Visibility = Visibility.Visible;

                var begin = TimeSpan.FromMilliseconds(i * 45);
                var dur = TimeSpan.FromMilliseconds(230);
                // 从图标中心「飞」出去
                it.Move.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(Half - cx, 0, dur) { BeginTime = begin, EasingFunction = ease });
                it.Move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(Half - cy, 0, dur) { BeginTime = begin, EasingFunction = ease });
                it.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0.3, 1, dur) { BeginTime = begin, EasingFunction = ease });
                it.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.3, 1, dur) { BeginTime = begin, EasingFunction = ease });
                it.View.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { BeginTime = begin });
            }
        }

        void CloseFan()
        {
            if (!_fanOpen) return;
            _fanOpen = false;
            FanHit.Visibility = Visibility.Collapsed;
            var dur = TimeSpan.FromMilliseconds(130);
            foreach (var it in _items)
            {
                var view = it.View;
                var fade = new DoubleAnimation(0, dur);
                fade.Completed += (s, e) => { if (!_fanOpen) view.Visibility = Visibility.Collapsed; };
                view.BeginAnimation(OpacityProperty, fade);
                it.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, dur));
                it.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, dur));
            }
        }

        // ================= 右键菜单 =================

        void ShowMenu()
        {
            _openTimer.Stop();
            CloseFan();
            var menu = new ContextMenu();

            var settings = new MenuItem { Header = "设置…" };
            settings.Click += (s, e) => App.OpenSettings();
            menu.Items.Add(settings);

            var newChat = new MenuItem { Header = "开始新的 AI 对话" };
            newChat.Click += (s, e) => InputBar.ResetChat();
            menu.Items.Add(newChat);

            menu.Items.Add(new Separator());
            foreach (var (mode, label) in Options)
            {
                var m = mode;
                var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = App.Settings.Mode == m };
                mi.Click += (s, e) => Choose(m);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());

            var auto = new MenuItem { Header = "开机自动启动", IsCheckable = true, IsChecked = AppSettings.AutoStart };
            auto.Click += (s, e) => AppSettings.AutoStart = auto.IsChecked;
            menu.Items.Add(auto);

            var quit = new MenuItem { Header = "退出" };
            quit.Click += (s, e) => Application.Current.Shutdown();
            menu.Items.Add(quit);

            menu.PlacementTarget = BubbleHost;
            menu.IsOpen = true;
        }
    }
}
