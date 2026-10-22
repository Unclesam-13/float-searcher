using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
        long _autoHiddenAt;

        // 对话记录
        readonly List<ChatMsg> _chat = new List<ChatMsg>();
        readonly List<ChatMsg> _clip = new List<ChatMsg>();
        string _clipText;        // 当前剪贴板对话针对的内容
        string _ownCopy;         // 自己复制出去的文字（不当成新的剪贴板内容）
        bool _clipExpanded;

        // 翻译结果缓存（指令 + 原文 → 译文）
        static readonly Dictionary<string, string> TrCache = new Dictionary<string, string>();

        public InputBar()
        {
            InitializeComponent();
            Current = this;

            Input.TextChanged += (s, e) => UpdatePlaceholder();
            Input.PreviewKeyDown += OnInputKey;
            SendBtn.Click += (s, e) => { if (_busy) StopAnswer(); else Submit(); };
            CloseBtn.Click += (s, e) => HideBar();
            NewBtn.Click += (s, e) => NewConversation();
            ClipCard.MouseLeftButtonUp += (s, e) =>
            {
                _clipExpanded = !_clipExpanded;
                ClipText.MaxHeight = _clipExpanded ? 260 : 58;
            };

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; HideBar(); } };
            Deactivated += (s, e) => OnDeactivated();
            SizeChanged += (s, e) => Reposition();
            SourceInitialized += (s, e) => Native.HideFromAltTab(this);
        }

        // ================= 显示 / 隐藏 =================

        /// <summary>点悬浮图标：没开就打开当前模式；开着且是同一模式就收起</summary>
        public static void Toggle(BubbleWindow bubble)
        {
            var bar = Current ?? new InputBar();
            var mode = App.Settings.Mode;
            if (bar.IsVisible && bar._mode == mode) { bar.HideBar(); return; }
            // 刚因为点到别处自动收起（点的正是悬浮图标）：当作「收起」，不再重新打开
            if (!bar.IsVisible && bar._mode == mode && Environment.TickCount64 - bar._autoHiddenAt < 400) return;
            bar.ShowFor(mode);
        }

        /// <summary>扇形里换了模式：输入窗开着就跟着切换</summary>
        public void SwitchMode(Mode m)
        {
            if (IsVisible) ShowFor(m);
        }

        public static void ResetChat()
        {
            var bar = Current;
            if (bar == null) return;
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
            if (m == Mode.Translate && Input.Text.Length > 0) Input.SelectAll();
            else Input.CaretIndex = Input.Text.Length;

            // 剪贴板 + 自动发送：拿到新内容就直接问
            if (m == Mode.Clipboard && _pendingAutoSend)
            {
                _pendingAutoSend = false;
                SendClip(App.Settings.ClipPrompt, App.Settings.ClipPrompt, includeText: true);
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
                _ => _clipText == null ? "先复制一段文字，再点悬浮图标"
                   : (_clip.Count == 0 ? "对这段内容说点什么，回车发送" : "继续追问，回车发送"),
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
                    if (_clipText == null) { SetStatus("剪贴板里没有文字。先复制，再点悬浮图标。", true); return; }
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
            _ai.Cancel();
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
            if (TrCache.TryGetValue(key, out var cached))
            {
                StopAnswer();
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
                    piece => { acc.Append(piece); RenderTranslation(acc.ToString(), done: false); },
                    noThinking: true);
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
            catch (OperationCanceledException) { SetStatus(null); }
            catch (AiException e) { SetStatus(e.Message, true); }
            catch (Exception e) { SetStatus("出错了：" + e.Message, true); }
            finally { SetBusy(false); }
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

        async Task Converse(List<ChatMsg> history, StackPanel panel, string display, string content, string system)
        {
            if (_busy) StopAnswer();
            AddBubble(panel, display, user: true);
            history.Add(new ChatMsg("user", content));
            // 只带最近 20 条，避免太长
            while (history.Count > 20) history.RemoveAt(0);

            var answerBox = AddBubble(panel, "…", user: false);
            answerBox.Foreground = App.Brush("InkSoft");
            SetBusy(true);
            SetStatus("思考中…");
            UpdateOutputVisibility();
            ScrollToEnd();

            var acc = new StringBuilder();
            try
            {
                var full = await _ai.StreamAsync(App.Settings, system, history.ToArray(),
                    piece =>
                    {
                        acc.Append(piece);
                        answerBox.Foreground = App.Brush("Ink");
                        answerBox.Text = CleanMd(acc.ToString());
                        SetStatus("正在回答…");
                        ScrollToEnd();
                    },
                    onThinking: n => { if (acc.Length == 0) SetStatus($"深度思考中…（已想 {n} 字）"); });
                if (string.IsNullOrWhiteSpace(full))
                {
                    answerBox.Text = "（没有收到回答）";
                    history.RemoveAt(history.Count - 1);
                }
                else
                {
                    answerBox.Text = CleanMd(full);
                    history.Add(new ChatMsg("assistant", full));
                }
                SetStatus(null);
            }
            catch (OperationCanceledException)
            {
                if (acc.Length > 0) history.Add(new ChatMsg("assistant", acc.ToString()));
                else { answerBox.Text = "（已停止）"; history.RemoveAt(history.Count - 1); }
                SetStatus("已停止");
            }
            catch (Exception e)
            {
                var msg = e is AiException ? e.Message : "出错了：" + e.Message;
                if (acc.Length == 0)
                {
                    answerBox.Text = "⚠ " + msg;
                    answerBox.Foreground = App.Brush("Danger");
                    history.RemoveAt(history.Count - 1); // 撤回这条，方便重试
                }
                SetStatus(msg, true);
            }
            finally
            {
                SetBusy(false);
                UpdatePlaceholder();
                ScrollToEnd();
            }
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

        /// <summary>读剪贴板：内容变了就开始一段新对话</summary>
        void PrepareClipboard()
        {
            var text = ReadClipboard();
            bool isNew = text != null && text != _clipText && text != _ownCopy;
            if (isNew)
            {
                if (_busy) StopAnswer();
                _clipText = text;
                _clip.Clear();
                ClipPanel.Children.Clear();
                _clipExpanded = false;
                ClipText.MaxHeight = 58;
                // 「粘贴后自动发送」：显示出来后立刻用预设提示词发送
                _pendingAutoSend = App.Settings.ClipAction == ClipAction.AutoSend;
            }

            if (_clipText == null)
            {
                SetStatus("剪贴板里没有文字。先复制一段，再点悬浮图标。", true);
                return;
            }
            ClipCard.Visibility = Visibility.Visible;
            ClipTitle.Text = $"剪贴板 · {_clipText.Length} 字";
            ClipText.Text = _clipText;
            RenderChips();
        }

        void RenderChips()
        {
            Chips.Children.Clear();
            foreach (var p in App.Settings.ClipPresetList())
            {
                var prompt = p.Value;
                var name = p.Key;
                var b = new Button { Content = name, Style = (Style)FindResource("ChipBtn"), ToolTip = prompt };
                b.Click += (s, e) => SendClip(name, prompt, includeText: true);
                Chips.Children.Add(b);
            }
            Chips.Visibility = _clipText != null && Chips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>对剪贴板内容提问：第一次提问或点快捷按钮时，把原文一起带上</summary>
        void SendClip(string display, string prompt, bool includeText)
        {
            if (_clipText == null) return;
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
