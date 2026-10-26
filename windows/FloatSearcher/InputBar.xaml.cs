using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatSearcher
{
    /// <summary>
    /// 点悬浮图标后，横向出现的输入窗。两种模式：
    ///  - 中译英：回车直接显示译文（可附同义词、不同句式等，在设置里选）
    ///  - AI 对话：Mindflow 式的追问对话（见 InputBar.Mind.cs）——
    ///    在回答里划线追问、问答树、历史，可以粘贴图片
    /// 窗口只隐藏不销毁。
    /// </summary>
    public partial class InputBar : Window
    {
        public static InputBar Current { get; private set; }

        readonly AiClient _ai = new AiClient(); // 中译英用
        Mode _mode;
        bool _busy;            // 中译英正在请求
        int _gen;              // 每次翻译 +1；旧请求的收尾不会改动新请求的界面
        long _autoHiddenAt;

        // 翻译结果缓存（指令 + 原文 → 译文）
        static readonly Dictionary<string, string> TrCache = new Dictionary<string, string>();

        public InputBar()
        {
            InitializeComponent();
            Current = this;

            Input.TextChanged += (s, e) => UpdatePlaceholder();
            Input.PreviewKeyDown += OnInputKey;
            SendBtn.Click += (s, e) => OnSendClick();
            CloseBtn.Click += (s, e) => HideBar();
            WebBtn.Click += (s, e) =>
            {
                App.Settings.WebSearch = !App.Settings.WebSearch;
                App.Settings.Save();
                UpdateWebBtn();
                SetStatus(App.Settings.WebSearch ? "已开启联网：需要时会先搜索，回答附来源" : "已关闭联网");
            };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (!MindEscape()) HideBar();
            };
            Deactivated += (s, e) => OnDeactivated();
            SizeChanged += (s, e) => Reposition();
            SourceInitialized += (s, e) => Native.HideFromAltTab(this);

            MindInit();
        }

        // ================= 显示 / 隐藏 =================

        /// <summary>
        /// 按下悬浮图标那一刻输入窗是不是开着（包括「刚因为点到图标而自动收起」）。
        /// 在鼠标按下时记下来，松开时据此决定是打开还是收起。
        /// </summary>
        public static bool WasOpen()
        {
            var bar = Current;
            if (bar == null || bar._mode != App.Settings.Mode) return false;
            return bar.IsVisible || Environment.TickCount64 - bar._autoHiddenAt < 600;
        }

        /// <summary>点悬浮图标：没开就打开当前模式；开着就收起</summary>
        public static void Toggle(bool wasOpen)
        {
            var bar = Current ?? new InputBar();
            if (wasOpen)
            {
                if (bar.IsVisible) bar.HideBar();
                return;
            }
            bar.ShowFor(App.Settings.Mode);
        }

        /// <summary>扇形里换了模式：输入窗开着（或刚被这次点击收起）就跟着切换</summary>
        public static void OnModeChosen(Mode m)
        {
            var bar = Current;
            if (bar == null) return;
            if (bar.IsVisible || Environment.TickCount64 - bar._autoHiddenAt < 600) bar.ShowFor(m);
        }

        /// <summary>右键菜单「开始新的 AI 对话」</summary>
        public static void ResetChat() => Current?.MindNew();

        public static void ClearTranslateCache() => TrCache.Clear();

        void ShowFor(Mode m)
        {
            bool modeChanged = m != _mode || !IsVisible;
            if (_busy && m != _mode) StopTranslate();
            _mode = m;
            bool chat = m == Mode.Chat;

            ModeLabel.Text = chat ? "AI 对话" : "中 → 英";
            TranslatePanel.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
            ChatPanel.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            var cv = chat ? Visibility.Visible : Visibility.Collapsed;
            AttachBtn.Visibility = cv;
            TreeBtn.Visibility = cv;
            HistBtn.Visibility = cv;
            NewBtn.Visibility = cv;
            UpdateWebBtn();
            SetStatus(null);

            if (!chat && modeChanged)
            {
                // 每次打开都是一个干净的输入框
                Input.Text = "";
                TranslatePanel.Children.Clear();
            }
            if (chat) MindOnShow();
            else MindHideChrome();

            UpdateLayoutForMode();
            UpdatePlaceholder();
            UpdateOutputVisibility();
            UpdateSendBtn();

            Reposition(); // 先摆好位置再显示，避免闪一下
            if (!IsVisible) Show();
            Reposition();
            Activate();
            Input.Focus();
            Keyboard.Focus(Input);
            Dispatcher.BeginInvoke(new Action(() => { Input.Focus(); Keyboard.Focus(Input); }),
                System.Windows.Threading.DispatcherPriority.Input);
            if (!chat && Input.Text.Length > 0) Input.SelectAll();
            else Input.CaretIndex = Input.Text.Length;
        }

        void HideBar()
        {
            if (_mode == Mode.Translate && _busy) StopTranslate();
            MindClosePopup();
            Hide();
        }

        void OnDeactivated()
        {
            // 中译英是「查完就走」：点到别处自动收起（设置里可关）
            if (IsVisible && _mode == Mode.Translate && App.Settings.TrAutoHide)
            {
                _autoHiddenAt = Environment.TickCount64;
                HideBar();
            }
        }

        /// <summary>中译英窄一些；AI 对话宽一些，打开侧栏时再加宽</summary>
        void UpdateLayoutForMode()
        {
            if (_mode == Mode.Chat)
            {
                Width = _sideOpen ? 960 : 690;
                if (_sideOpen)
                {
                    OutGrid.Height = 480;
                    OutScroll.MaxHeight = double.PositiveInfinity;
                }
                else
                {
                    OutGrid.Height = double.NaN;
                    OutScroll.MaxHeight = 480;
                }
            }
            else
            {
                Width = 600;
                OutGrid.Height = double.NaN;
                OutScroll.MaxHeight = 320;
                Side.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>贴着悬浮图标横向展开：图标在右半屏就往左开，否则往右开</summary>
        public void Reposition()
        {
            if (App.Bubble == null) return;
            var br = App.Bubble.BubbleRect;
            var c = new Point(br.Left + br.Width / 2, br.Top + br.Height / 2);
            var wa = Native.WorkAreaAt(App.Bubble, c);
            double w = ActualWidth > 0 ? Math.Max(ActualWidth, Width) : Width;
            if (double.IsNaN(w)) w = 600;
            double h = ActualHeight > 0 ? ActualHeight : 80;

            double left = c.X > wa.Left + wa.Width / 2 ? br.Left - w + 6 : br.Right - 6;
            left = Math.Max(wa.Left, Math.Min(wa.Right - w, left));
            // 输入行和图标对齐；内容变长、放不下时整体往上挪
            double top = c.Y - 36;
            top = Math.Max(wa.Top, Math.Min(wa.Bottom - h, top));
            Left = left;
            Top = top;
        }

        // ================= 输入 =================

        void UpdatePlaceholder()
        {
            Placeholder.Text = _mode == Mode.Chat ? MindPlaceholder() : "输入中文，回车翻译成英文";
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnInputKey(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            // Ctrl+V / Shift+Insert：剪贴板里是图片就当附件
            if (_mode == Mode.Chat &&
                ((e.Key == Key.V && mods == ModifierKeys.Control) || (e.Key == Key.Insert && mods == ModifierKeys.Shift)))
            {
                if (TryPasteImage()) e.Handled = true;
                return;
            }
            // 输入法正在拼字时按回车（Key.ImeProcessed）不算发送
            if (e.Key != Key.Enter) return;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            if (shift && _mode != Mode.Translate) return; // 换行
            e.Handled = true;
            Submit();
        }

        void OnSendClick()
        {
            if (_mode == Mode.Chat)
            {
                if (MindCurrentRunning) { MindStopCurrent(); SetStatus("已停止"); }
                else Submit();
            }
            else if (_busy) { StopTranslate(); SetStatus("已停止"); }
            else Submit();
        }

        void Submit()
        {
            var text = Input.Text.Trim();
            if (_mode == Mode.Translate)
            {
                if (text.Length > 0) Translate(text);
            }
            else MindSubmit(text);
        }

        void StopTranslate()
        {
            _gen++;
            _ai.Cancel();
            SetBusy(false);
        }

        void SetBusy(bool on)
        {
            _busy = on;
            UpdateSendBtn();
        }

        void UpdateSendBtn()
        {
            bool on = _mode == Mode.Chat ? MindCurrentRunning : _busy;
            SendBtn.Content = on ? "■" : "↵";
            SendBtn.ToolTip = on ? "停止" : "发送（回车）";
        }

        void SetStatus(string text, bool error = false)
        {
            if (string.IsNullOrEmpty(text)) { Status.Visibility = Visibility.Collapsed; return; }
            Status.Text = text;
            Status.Foreground = App.Brush(error ? "Danger" : "InkSoft");
            Status.Visibility = Visibility.Visible;
        }

        void UpdateOutputVisibility()
        {
            bool show = _mode == Mode.Chat
                ? ChatPanel.Children.Count > 0 || _sideOpen
                : TranslatePanel.Children.Count > 0;
            OutGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < 3; i++)
            {
                try { Clipboard.SetText(text); SetStatus("已复制：" + OneLine(text, 40)); return; }
                catch { Thread.Sleep(40); }
            }
            SetStatus("复制失败，剪贴板被别的程序占用了，再试一次", true);
        }

        static string OneLine(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        /// <summary>只有 DeepSeek 接口能联网</summary>
        static bool WebAvailable => App.Settings.Provider == Providers.DeepSeek;

        void UpdateWebBtn()
        {
            bool show = _mode == Mode.Chat && WebAvailable;
            WebBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            bool on = App.Settings.WebSearch;
            WebBtn.Content = on ? "联网 ✓" : "联网";
            WebBtn.Foreground = App.Brush(on ? "Accent" : "InkSoft");
            WebBtn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // ================= 中译英 =================

        async void Translate(string text)
        {
            var prompt = Prompts.Translate(App.Settings);
            var key = prompt + "\u0001" + text;
            int gen = ++_gen;
            if (TrCache.TryGetValue(key, out var cached))
            {
                _ai.Cancel();
                SetBusy(false);
                RenderTranslation(cached, done: true);
                Input.SelectAll();
                return;
            }

            SetBusy(true);
            SetStatus("翻译中…");
            TranslatePanel.Children.Clear();
            UpdateOutputVisibility();
            var acc = new StringBuilder();
            try
            {
                var full = await _ai.StreamAsync(App.Settings, prompt,
                    new List<ChatMsg> { new ChatMsg("user", text) },
                    piece =>
                    {
                        if (gen != _gen) return; // 已经在查下一个了
                        acc.Append(piece);
                        RenderTranslation(acc.ToString(), done: false);
                    },
                    noThinking: true);
                if (gen != _gen) return;
                if (string.IsNullOrWhiteSpace(full))
                {
                    SetStatus("没有拿到译文，再按一次回车试试", true);
                }
                else
                {
                    TrCache[key] = full;
                    RenderTranslation(full, done: true);
                }
                if (_mode == Mode.Translate) Input.SelectAll(); // 接着打字就是查下一个
            }
            catch (OperationCanceledException) { if (gen == _gen) SetStatus(null); }
            catch (AiException e) { if (gen == _gen) SetStatus(e.Message, true); }
            catch (Exception e) { if (gen == _gen) SetStatus("出错了：" + e.Message, true); }
            finally { if (gen == _gen) SetBusy(false); }
        }

        /// <summary>第一行大字是主译文，后面每行是同义词 / 各种句式；点哪一行就复制哪一行</summary>
        void RenderTranslation(string answer, bool done)
        {
            if (_mode != Mode.Translate) return;
            TranslatePanel.Children.Clear();
            var lines = Prompts.Split(answer);
            for (int i = 0; i < lines.Count; i++)
            {
                var ln = lines[i];
                bool main = i == 0;
                var row = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, main ? 6 : 5, 8, main ? 6 : 5),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = "点击复制",
                };
                var dock = new DockPanel();
                if (!string.IsNullOrEmpty(ln.Label))
                {
                    var tag = new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(7, 1, 7, 1),
                        Margin = new Thickness(0, 1, 8, 0),
                        Background = App.Brush("Surface2"),
                        VerticalAlignment = VerticalAlignment.Top,
                        Child = new TextBlock
                        {
                            Text = ln.Label,
                            FontSize = 11.5,
                            Foreground = App.Brush("InkSoft"),
                        },
                    };
                    DockPanel.SetDock(tag, Dock.Left);
                    dock.Children.Add(tag);
                }
                dock.Children.Add(new TextBlock
                {
                    Text = ln.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = main ? 19 : 14,
                    FontWeight = main ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = App.Brush("Ink"),
                });
                row.Child = dock;
                var copy = ln.Text;
                row.MouseEnter += (s, e) => row.Background = App.Brush("Hover");
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (s, e) => CopyText(copy);
                TranslatePanel.Children.Add(row);
            }
            UpdateOutputVisibility();
            if (done) SetStatus(lines.Count > 1 ? "点任意一行复制 · 接着打字查下一个" : "点译文复制 · 接着打字查下一个");
        }

        // ================= 图片 =================

        static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        static bool IsImageFile(string f) =>
            !string.IsNullOrEmpty(f) && Array.IndexOf(ImageExts, Path.GetExtension(f).ToLowerInvariant()) >= 0;

        static BitmapSource LoadImageFile(string f)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(f);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        /// <summary>读剪贴板里的图片：截图、网页里「复制图片」、资源管理器里复制的图片文件</summary>
        static BitmapSource ReadClipboardImage()
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    if (Clipboard.ContainsFileDropList())
                    {
                        foreach (var f in Clipboard.GetFileDropList())
                            if (IsImageFile(f) && File.Exists(f)) return LoadImageFile(f);
                        return null;
                    }
                    // 浏览器等会放一份 PNG（保留透明度，比 DIB 可靠）
                    if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream ms)
                    {
                        ms.Position = 0;
                        var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var fr = dec.Frames[0];
                        fr.Freeze();
                        return fr;
                    }
                    if (Clipboard.ContainsImage())
                    {
                        var img = Clipboard.GetImage();
                        if (img != null) { img.Freeze(); return img; }
                    }
                    return null;
                }
                catch { Thread.Sleep(50); }
            }
            return null;
        }

        /// <summary>最长边不超过 1568 像素，透明处垫白底，JPEG 质量 85</summary>
        static byte[] EncodeJpeg(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w < 1 || h < 1) return null;
            double scale = Math.Min(1.0, 1568.0 / Math.Max(w, h));
            int tw = Math.Max(1, (int)Math.Round(w * scale));
            int th = Math.Max(1, (int)Math.Round(h * scale));

            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, tw, th));
                dc.DrawImage(src, new Rect(0, 0, tw, th));
            }
            var rtb = new RenderTargetBitmap(tw, th, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var outMs = new MemoryStream();
            enc.Save(outMs);
            return outMs.ToArray();
        }

        protected override void OnClosed(EventArgs e)
        {
            _ai.Cancel();
            MindShutdown();
            if (Current == this) Current = null;
            base.OnClosed(e);
        }
    }
}
