using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Documents;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace FloatSearcher
{
    /// <summary>
    /// 点悬浮图标后，横向出现的输入窗。三种模式：
    ///  - 中译英：回车直接显示译文（可附同义词、不同句式等，在设置里选）
    ///  - AI 对话：回车发送，多轮对话
    ///  - 剪贴板：自动带上刚复制的内容；可自动用预设提示词发送，或等你输入提示词
    /// 窗口只隐藏不销毁，AI 对话的内容会保留，直到点「新对话」。
    /// </summary>
    public partial class InputBar : Window
    {
        public static InputBar Current { get; private set; }

        readonly AiClient _ai = new AiClient();
        Mode _mode;
        bool _busy;
        int _gen;              // 每次提问 +1；旧请求的收尾不会改动新请求的界面
        long _autoHiddenAt;

        // 对话记录
        readonly List<ChatMsg> _chat = new List<ChatMsg>();
        readonly List<ChatMsg> _clip = new List<ChatMsg>();
        string _clipText;        // 当前剪贴板对话针对的内容
        string _ownCopy;         // 自己复制出去的文字（不当成新的剪贴板内容）
        bool _clipExpanded;
        BitmapSource _clipImage;  // 当前剪贴板对话针对的图片（和 _clipText 只会有一个）
        string _clipImageB64;     // 压缩后的 JPEG
        string _clipImageInfo;    // 「1920×1080」
        uint _clipImageSeq;       // 读到这张图时的剪贴板序号
        bool HasClip => _clipText != null || _clipImageB64 != null;

        // 翻译结果缓存（指令 + 原文 → 译文）
        static readonly Dictionary<string, string> TrCache = new Dictionary<string, string>();

        public InputBar()
        {
            InitializeComponent();
            Current = this;

            Input.TextChanged += (s, e) => UpdatePlaceholder();
            Input.PreviewKeyDown += OnInputKey;
            SendBtn.Click += (s, e) => { if (_busy) { StopAnswer(); SetStatus("已停止"); } else Submit(); };
            CloseBtn.Click += (s, e) => HideBar();
            NewBtn.Click += (s, e) => NewConversation();
            WebBtn.Click += (s, e) =>
            {
                App.Settings.WebSearch = !App.Settings.WebSearch;
                App.Settings.Save();
                UpdateWebBtn();
                SetStatus(App.Settings.WebSearch ? "已开启联网：需要时会先搜索，回答附来源" : "已关闭联网");
            };
            ClipCard.MouseLeftButtonUp += (s, e) =>
            {
                _clipExpanded = !_clipExpanded;
                ClipText.MaxHeight = _clipExpanded ? 260 : 58;
                ClipImage.MaxHeight = _clipExpanded ? 320 : 96;
            };

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; HideBar(); } };
            Deactivated += (s, e) => OnDeactivated();
            SizeChanged += (s, e) => Reposition();
            SourceInitialized += (s, e) => Native.HideFromAltTab(this);
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

        public static void ResetChat()
        {
            var bar = Current;
            if (bar == null) return;
            if (bar._busy && bar._mode == Mode.Chat) bar.StopAnswer();
            bar._chat.Clear();
            bar.ChatPanel.Children.Clear();
            if (bar._mode == Mode.Chat) bar.UpdateOutputVisibility();
        }

        public static void ClearTranslateCache() => TrCache.Clear();

        void ShowFor(Mode m)
        {
            bool modeChanged = m != _mode || !IsVisible;
            if (_busy && m != _mode) StopAnswer();
            _mode = m;

            ModeLabel.Text = m switch { Mode.Translate => "中 → 英", Mode.Chat => "AI 对话", _ => "剪贴板" };
            TranslatePanel.Visibility = m == Mode.Translate ? Visibility.Visible : Visibility.Collapsed;
            ChatPanel.Visibility = m == Mode.Chat ? Visibility.Visible : Visibility.Collapsed;
            ClipPanel.Visibility = m == Mode.Clipboard ? Visibility.Visible : Visibility.Collapsed;
            NewBtn.Visibility = m == Mode.Translate ? Visibility.Collapsed : Visibility.Visible;
            UpdateWebBtn();
            OutScroll.MaxHeight = m == Mode.Translate ? 320 : 440;
            ClipCard.Visibility = Visibility.Collapsed;
            Chips.Visibility = Visibility.Collapsed;
            SetStatus(null);

            if (m == Mode.Translate && modeChanged)
            {
                // 每次打开都是一个干净的输入框
                Input.Text = "";
                TranslatePanel.Children.Clear();
            }
            if (m == Mode.Clipboard) PrepareClipboard();

            UpdatePlaceholder();
            UpdateOutputVisibility();

            Reposition(); // 先摆好位置再显示，避免闪一下
            if (!IsVisible) Show();
            Reposition();
            Activate();
            Input.Focus();
            Keyboard.Focus(Input);
            Dispatcher.BeginInvoke(new Action(() => { Input.Focus(); Keyboard.Focus(Input); }),
                System.Windows.Threading.DispatcherPriority.Input);
            if (m == Mode.Translate && Input.Text.Length > 0) Input.SelectAll();
            else Input.CaretIndex = Input.Text.Length;

            // 剪贴板 + 自动发送：拿到新内容就直接问
            if (m == Mode.Clipboard && _pendingAutoSend)
            {
                _pendingAutoSend = false;
                var prompt = _clipImageB64 != null ? App.Settings.ClipImagePrompt : App.Settings.ClipPrompt;
                SendClip(prompt, prompt, includeText: true);
            }
        }

        void HideBar()
        {
            if (_mode == Mode.Translate && _busy) StopAnswer();
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

        /// <summary>贴着悬浮图标横向展开：图标在右半屏就往左开，否则往右开</summary>
        public void Reposition()
        {
            if (App.Bubble == null) return;
            var br = App.Bubble.BubbleRect;
            var c = new Point(br.Left + br.Width / 2, br.Top + br.Height / 2);
            var wa = Native.WorkAreaAt(App.Bubble, c);
            double w = ActualWidth > 0 ? ActualWidth : Width;
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
            Placeholder.Text = _mode switch
            {
                Mode.Translate => "输入中文，回车翻译成英文",
                Mode.Chat => "问点什么，回车发送（Shift+回车换行）",
                _ => !HasClip ? "先复制一段文字或一张图片，再点悬浮图标"
                   : (_clip.Count == 0 ? (_clipImageB64 != null ? "对这张图片说点什么，回车发送" : "对这段内容说点什么，回车发送") : "继续追问，回车发送"),
            };
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnInputKey(object sender, KeyEventArgs e)
        {
            // 输入法正在拼字时按回车（Key.ImeProcessed）不算发送
            if (e.Key != Key.Enter) return;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (shift && _mode != Mode.Translate) return; // 换行
            e.Handled = true;
            Submit();
        }

        void Submit()
        {
            var text = Input.Text.Trim();
            switch (_mode)
            {
                case Mode.Translate:
                    if (text.Length > 0) Translate(text);
                    break;
                case Mode.Chat:
                    if (text.Length == 0) return;
                    Input.Text = "";
                    _ = Converse(_chat, ChatPanel, text, text, App.Settings.ChatSystem);
                    break;
                case Mode.Clipboard:
                    if (!HasClip) { SetStatus("剪贴板里没有文字或图片。先复制，再点悬浮图标。", true); return; }
                    if (text.Length == 0) return;
                    Input.Text = "";
                    SendClip(text, text, includeText: _clip.Count == 0);
                    break;
            }
        }

        void NewConversation()
        {
            StopAnswer();
            if (_mode == Mode.Chat)
            {
                _chat.Clear();
                ChatPanel.Children.Clear();
            }
            else if (_mode == Mode.Clipboard)
            {
                _clip.Clear();
                ClipPanel.Children.Clear();
                RenderChips();
            }
            SetStatus(null);
            UpdatePlaceholder();
            UpdateOutputVisibility();
            Input.Focus();
        }

        void StopAnswer()
        {
            _gen++;
            _ai.Cancel();
            SetBusy(false);
        }

        void SetBusy(bool on)
        {
            _busy = on;
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
            var panel = _mode switch { Mode.Translate => TranslatePanel, Mode.Chat => ChatPanel, _ => ClipPanel };
            OutScroll.Visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < 3; i++)
            {
                try { Clipboard.SetText(text); _ownCopy = text; SetStatus("已复制：" + OneLine(text, 40)); return; }
                catch { Thread.Sleep(40); }
            }
            SetStatus("复制失败，剪贴板被别的程序占用了，再试一次", true);
        }

        static string OneLine(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
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

        // ================= AI 对话（AI 对话、剪贴板共用） =================

        async Task Converse(List<ChatMsg> history, StackPanel panel, string display, string content, string system,
                            string image = null)
        {
            if (_busy) StopAnswer();
            int gen = ++_gen;
            AddBubble(panel, image != null ? "🖼 " + display : display, user: true);
            var userMsg = new ChatMsg("user", content) { Image = image };
            history.Add(userMsg);
            TrimHistory(history, keepHead: ReferenceEquals(history, _clip));

            var answerBox = AddBubble(panel, "…", user: false);
            answerBox.Foreground = App.Brush("InkSoft");
            SetBusy(true);
            SetStatus("思考中…");
            UpdateOutputVisibility();
            ScrollToEnd();

            var acc = new StringBuilder();
            List<SearchHit> sources = null;
            WebCallbacks web = null;
            if (WebAvailable && App.Settings.WebSearch)
            {
                web = new WebCallbacks
                {
                    OnSearch = q =>
                    {
                        if (acc.Length == 0) { answerBox.Foreground = App.Brush("InkSoft"); answerBox.Text = "正在搜索：" + q + " …"; }
                        if (gen == _gen) SetStatus("正在联网搜索：" + q);
                    },
                    OnReset = () => { acc.Clear(); },
                    OnSources = list => sources = list,
                };
            }
            try
            {
                var full = await _ai.StreamAsync(App.Settings, system, history.ToArray(),
                    piece =>
                    {
                        acc.Append(piece);
                        answerBox.Foreground = App.Brush("Ink");
                        answerBox.Text = CleanMd(acc.ToString());
                        if (gen == _gen) { SetStatus("正在回答…"); ScrollToEnd(); }
                    },
                    onThinking: n => { if (gen == _gen && acc.Length == 0) SetStatus($"深度思考中…（已想 {n} 字）"); },
                    web: web);
                if (string.IsNullOrWhiteSpace(full))
                {
                    answerBox.Text = "（没有收到回答）";
                    Settle(history, userMsg, null);
                }
                else
                {
                    answerBox.Text = CleanMd(full);
                    Settle(history, userMsg, full);
                    if (sources != null && sources.Count > 0) AddSources(panel, answerBox, sources);
                }
                if (gen == _gen) SetStatus(null);
            }
            catch (OperationCanceledException)
            {
                if (acc.Length == 0) answerBox.Text = "（已停止）";
                Settle(history, userMsg, acc.Length > 0 ? acc.ToString() : null);
            }
            catch (Exception e)
            {
                var msg = e is AiException ? e.Message : "出错了：" + e.Message;
                if (acc.Length == 0)
                {
                    answerBox.Text = "⚠ " + msg;
                    answerBox.Foreground = App.Brush("Danger");
                }
                Settle(history, userMsg, acc.Length > 0 ? acc.ToString() : null);
                if (gen == _gen) SetStatus(msg, true);
            }
            finally
            {
                if (gen == _gen)
                {
                    SetBusy(false);
                    UpdatePlaceholder();
                    ScrollToEnd();
                }
            }
        }

        /// <summary>只有 DeepSeek 接口能联网</summary>
        static bool WebAvailable => App.Settings.Provider == Providers.DeepSeek;

        void UpdateWebBtn()
        {
            bool show = _mode != Mode.Translate && WebAvailable;
            WebBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            bool on = App.Settings.WebSearch;
            WebBtn.Content = on ? "联网 ✓" : "联网";
            WebBtn.Foreground = App.Brush(on ? "Accent" : "InkSoft");
            WebBtn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        /// <summary>在回答下面列出引用的来源，点标题用浏览器打开</summary>
        void AddSources(StackPanel panel, TextBox answerBox, List<SearchHit> sources)
        {
            var box = new StackPanel { Margin = new Thickness(4, 6, 0, 0) };
            box.Children.Add(new TextBlock { Text = "来源", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = App.Brush("Accent") });
            foreach (var h in sources)
            {
                var tb = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Foreground = App.Brush("InkSoft") };
                tb.Inlines.Add(new Run("[" + h.Index + "] "));
                var link = new Hyperlink(new Run(string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title))
                {
                    Foreground = App.Brush("Accent"),
                    TextDecorations = null,
                    Cursor = Cursors.Hand,
                    ToolTip = h.Url,
                };
                var url = h.Url;
                link.Click += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { CopyText(url); }
                };
                tb.Inlines.Add(link);
                var meta = h.Site;
                if (!string.IsNullOrEmpty(h.Date) && h.Date.Length >= 10) meta = (meta.Length > 0 ? meta + " · " : "") + h.Date.Substring(0, 10);
                if (meta.Length > 0) tb.Inlines.Add(new Run("  " + meta));
                box.Children.Add(tb);
            }
            int idx = panel.Children.IndexOf(answerBox.Parent as UIElement);
            if (idx >= 0) panel.Children.Insert(idx + 1, box);
            else panel.Children.Add(box);
        }

        /// <summary>
        /// 把这一问的结果放回对话记录：有回答就紧跟在这条提问后面；没有回答就撤回这条提问（方便重试）。
        /// 对话已被清空、或已经开始了新的提问时也不会弄乱顺序。
        /// </summary>
        static void Settle(List<ChatMsg> history, ChatMsg userMsg, string answer)
        {
            int idx = history.IndexOf(userMsg);
            if (idx < 0) return; // 对话已经清空了
            if (string.IsNullOrWhiteSpace(answer)) history.RemoveAt(idx);
            else history.Insert(idx + 1, new ChatMsg("assistant", answer));
        }

        /// <summary>只带最近 20 条；开头必须是用户的话。剪贴板对话保留第一问一答（里面有原文）。</summary>
        static void TrimHistory(List<ChatMsg> h, bool keepHead)
        {
            int start = keepHead && h.Count > 2 && h[0].Role == "user" && h[1].Role == "assistant" ? 2 : 0;
            while (h.Count > 20 && h.Count - start > 2)
            {
                h.RemoveAt(start);
                if (h.Count - start > 1 && h[start].Role != "user") h.RemoveAt(start);
            }
            while (h.Count > 1 && h[0].Role != "user") h.RemoveAt(0);
        }

        /// <summary>对话气泡：自己的靠右、带底色；AI 的靠左。用只读文本框，可以选中复制。</summary>
        TextBox AddBubble(StackPanel panel, string text, bool user)
        {
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = App.Brush("Ink"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = user ? 13.5 : 14.5,
                Padding = new Thickness(0),
                SelectionBrush = App.Brush("Accent"),
            };
            // 只读文本框会吞掉滚轮：转给外面的滚动区
            box.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                OutScroll.ScrollToVerticalOffset(OutScroll.VerticalOffset - e.Delta / 3.0);
            };
            var wrap = new Border
            {
                Child = box,
                CornerRadius = new CornerRadius(12),
                Padding = user ? new Thickness(12, 7, 12, 7) : new Thickness(4, 4, 4, 4),
                Margin = new Thickness(user ? 60 : 0, panel.Children.Count == 0 ? 0 : 10, 0, 0),
                Background = user ? App.Brush("Surface2") : Brushes.Transparent,
                HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            };
            panel.Children.Add(wrap);
            return box;
        }

        void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() => OutScroll.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>小窗里不渲染 Markdown：去掉粗体、标题符号，列表换成圆点</summary>
        static string CleanMd(string s)
        {
            var sb = new StringBuilder();
            foreach (var raw in (s ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.Replace("**", "");
                var t = line.TrimStart();
                if (t.StartsWith("#")) line = t.TrimStart('#').TrimStart();
                else if (t.StartsWith("- ") || t.StartsWith("* ")) line = line.Substring(0, line.Length - t.Length) + "• " + t.Substring(2);
                else if (t == "---" || t == "***") line = "";
                sb.Append(line).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // ================= 剪贴板 =================

        bool _pendingAutoSend;

        static string ReadClipboard()
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) return null;
                    var t = Clipboard.GetText();
                    return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
                }
                catch { Thread.Sleep(50); }
            }
            return null;
        }

        static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" };

        /// <summary>读剪贴板里的图片：资源管理器里复制的图片文件、截图、网页里「复制图片」</summary>
        static BitmapSource ReadClipboardImage()
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    // 资源管理器里复制的图片文件
                    if (Clipboard.ContainsFileDropList())
                    {
                        foreach (var f in Clipboard.GetFileDropList())
                        {
                            if (f == null || Array.IndexOf(ImageExts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                            if (!File.Exists(f)) continue;
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            bi.UriSource = new Uri(f);
                            bi.EndInit();
                            bi.Freeze();
                            return bi;
                        }
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

        /// <summary>最长边不超过 1568 像素，透明处垫白底，JPEG 质量 85，转 base64</summary>
        static string EncodeJpeg(BitmapSource src)
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
            return Convert.ToBase64String(outMs.ToArray());
        }

        /// <summary>换了新的剪贴板内容：清掉旧对话</summary>
        void StartClip()
        {
            if (_busy) StopAnswer();
            _clip.Clear();
            ClipPanel.Children.Clear();
            _clipExpanded = false;
            ClipText.MaxHeight = 58;
            ClipImage.MaxHeight = 96;
            // 「粘贴后自动发送」：显示出来后立刻用预设提示词发送
            _pendingAutoSend = App.Settings.ClipAction == ClipAction.AutoSend;
        }

        /// <summary>读剪贴板：内容变了就开始一段新对话。同时有文字和图片时用文字</summary>
        void PrepareClipboard()
        {
            var text = ReadClipboard();
            if (text != null)
            {
                bool isNew = text != _clipText && text != _ownCopy;
                if (isNew || (_clipImageB64 != null && text != _ownCopy))
                {
                    StartClip();
                    _clipText = text;
                    _clipImage = null; _clipImageB64 = null; _clipImageInfo = null;
                }
            }
            else
            {
                // 没有文字：看看有没有图片（剪贴板序号没变就是同一张，不重复读）
                uint seq = Native.ClipboardSeq();
                if (_clipImageB64 == null || seq == 0 || seq != _clipImageSeq)
                {
                    var img = ReadClipboardImage();
                    string b64 = null;
                    if (img != null)
                    {
                        try { b64 = EncodeJpeg(img); } catch { b64 = null; }
                    }
                    if (b64 != null && b64 != _clipImageB64)
                    {
                        StartClip();
                        _clipText = null;
                        _clipImage = img;
                        _clipImageB64 = b64;
                        _clipImageInfo = $"{img.PixelWidth}×{img.PixelHeight}";
                    }
                    if (b64 != null) _clipImageSeq = seq;
                }
            }

            if (!HasClip)
            {
                SetStatus("剪贴板里没有文字或图片。先复制一段文字或一张图，再点悬浮图标。", true);
                return;
            }
            ClipCard.Visibility = Visibility.Visible;
            if (_clipImageB64 != null)
            {
                ClipTitle.Text = $"剪贴板 · 图片 {_clipImageInfo} · 点击放大";
                ClipText.Visibility = Visibility.Collapsed;
                ClipImage.Source = _clipImage;
                ClipImage.Visibility = Visibility.Visible;
            }
            else
            {
                ClipTitle.Text = $"剪贴板 · {_clipText.Length} 字";
                ClipText.Text = _clipText;
                ClipText.Visibility = Visibility.Visible;
                ClipImage.Source = null;
                ClipImage.Visibility = Visibility.Collapsed;
            }
            RenderChips();
            UpdatePlaceholder();
        }

        void RenderChips()
        {
            Chips.Children.Clear();
            var list = new List<KeyValuePair<string, string>>();
            if (_clipImageB64 != null)
                list.Add(new KeyValuePair<string, string>("提取文字", "把图片里的文字原样提取出来，保持原有的分段和顺序，不要解释。"));
            foreach (var p in App.Settings.ClipPresetList()) list.Add(new KeyValuePair<string, string>(p.Key, p.Value));
            foreach (var p in list)
            {
                var prompt = p.Value;
                var name = p.Key;
                var b = new Button { Content = name, Style = (Style)FindResource("ChipBtn"), ToolTip = prompt };
                b.Click += (s, e) => SendClip(name, prompt, includeText: true);
                Chips.Children.Add(b);
            }
            Chips.Visibility = HasClip && Chips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>对剪贴板内容提问：第一次提问或点快捷按钮时，把原文 / 图片一起带上</summary>
        void SendClip(string display, string prompt, bool includeText)
        {
            if (!HasClip) return;
            if (_clipImageB64 != null)
            {
                _ = Converse(_clip, ClipPanel, display, prompt, App.Settings.ChatSystem,
                             image: includeText ? _clipImageB64 : null);
                return;
            }
            string content = includeText
                ? prompt + "\n\n内容：\n\"\"\"\n" + _clipText + "\n\"\"\""
                : prompt;
            _ = Converse(_clip, ClipPanel, display, content, App.Settings.ChatSystem);
        }

        protected override void OnClosed(EventArgs e)
        {
            _ai.Cancel();
            if (Current == this) Current = null;
            base.OnClosed(e);
        }
    }
}
