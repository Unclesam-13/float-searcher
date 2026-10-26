using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FloatSearcher
{
    /// <summary>
    /// AI 对话 = Canvas Mindflow 的追问方式：
    ///  - 在回答里选中看不懂的一段 → 弹出追问小窗 → 追问在单独的分支里展开，主回答原样保留；
    ///    被划的句子变成黄色高亮，点它跳到那条追问；分支里还能继续划线，一层层往下挖
    ///  - 问答树：同一窗口里接着问的是 1、2、3…，划线分出来的是 1.1、1.1.1…，点哪跳哪
    ///  - 历史：每段对话自动保存（格式和 Obsidian 插件一样），随时打开、搜索、删除
    ///  - Ctrl+V 粘贴截图 / 图片，📎 选图片，或者把图片文件拖进来
    /// 多个回答可以同时生成（比如主回答还在写，就去追问上面的一句）。
    /// </summary>
    public partial class InputBar
    {
        readonly MindStore _store = MindStore.Shared;
        MindSession _session;          // 当前对话（还没提问时为空）
        string _nodeId;                // 当前看的窗口
        bool _mindLoaded;              // 第一次打开时载入最近的对话
        MindAnchor _replyAnchor;       // 正在追问的划线原文
        MindAnchor _selAnchor;         // 刚选中的文字
        bool _sideOpen;
        string _sideTab = "tree";

        const double AnswerFont = 14.5;
        const int MaxImages = 4;

        /// <summary>待发送的图片</summary>
        readonly List<(BitmapSource thumb, byte[] jpeg)> _pending = new List<(BitmapSource, byte[])>();

        class Running
        {
            public AiClient Client;
            public MindSession S;
            public MindNode Node;
            public MindMsg Msg;
            public string Status;
        }
        readonly Dictionary<string, Running> _running = new Dictionary<string, Running>();

        class MsgView
        {
            public MindNode Node;
            public MindMsg Msg;
            public StackPanel Box;
            public TextBlock Status;
            public RichTextBox Rtb;
            public StackPanel Extras;
        }
        readonly Dictionary<string, MsgView> _views = new Dictionary<string, MsgView>();
        readonly Dictionary<string, FrameworkElement> _userViews = new Dictionary<string, FrameworkElement>();
        readonly HashSet<string> _dirty = new HashSet<string>();
        DispatcherTimer _renderTimer;

        Popup _selPop;
        WrapPanel _selPopPanel;

        /// <summary>发给 AI 的图片（base64），按文件路径缓存</summary>
        static readonly Dictionary<string, string> ImgCache = new Dictionary<string, string>();

        MindNode CurNode =>
            _session != null && _nodeId != null && _session.Nodes.TryGetValue(_nodeId, out var n) ? n : null;

        bool MindCurrentRunning
        {
            get
            {
                var n = CurNode;
                return n != null && _running.Values.Any(r => r.S == _session && r.Node == n);
            }
        }

        bool IsCurrent(MindSession s, MindNode n) => _mode == Mode.Chat && s == _session && n != null && n.Id == _nodeId;

        // ================= 初始化 =================

        void MindInit()
        {
            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _renderTimer.Tick += (s, e) => FlushDirty();
            _store.Error += msg => SetStatus(msg, true);
            _store.InUse = id => (_session != null && _session.Id == id) || _running.Values.Any(r => r.S.Id == id);

            NewBtn.Click += (s, e) => MindNew();
            TreeBtn.Click += (s, e) => ToggleSide("tree");
            HistBtn.Click += (s, e) => ToggleSide("hist");
            TabTree.Click += (s, e) => { _sideTab = "tree"; RenderSide(); };
            TabHist.Click += (s, e) => { _sideTab = "hist"; RenderSide(); };
            SideClose.Click += (s, e) => ToggleSide(null);
            BackBtn.Click += (s, e) => GoUp();
            ReplyCancel.Click += (s, e) => { ClearReply(); Input.Focus(); };
            HistSearch.TextChanged += (s, e) =>
            {
                HistSearchHint.Visibility = HistSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_sideTab == "hist") RenderSide();
            };
            AttachBtn.Click += (s, e) => PickImages();

            // 把图片文件拖进来
            PreviewDragOver += (s, e) =>
            {
                if (_mode == Mode.Chat && DroppedImages(e.Data).Count > 0)
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            };
            PreviewDrop += (s, e) =>
            {
                if (_mode != Mode.Chat) return;
                var files = DroppedImages(e.Data);
                if (files.Count == 0) return;
                e.Handled = true;
                foreach (var f in files)
                {
                    try { AddPending(LoadImageFile(f)); }
                    catch { SetStatus("这张图片打不开：" + Path.GetFileName(f), true); }
                }
                Activate();
                Input.Focus();
            };
        }

        static List<string> DroppedImages(IDataObject data)
        {
            var list = new List<string>();
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files) if (IsImageFile(f)) list.Add(f);
            }
            catch { }
            return list;
        }

        /// <summary>每次打开 AI 对话</summary>
        void MindOnShow()
        {
            if (!_mindLoaded)
            {
                _mindLoaded = true;
                if (_session == null)
                {
                    var list = _store.List();
                    if (list.Count > 0) { _session = list[0]; _nodeId = _session.RootId; }
                }
                RenderMind();
            }
            else UpdateCrumb();
            ReplyCard.Visibility = _replyAnchor != null ? Visibility.Visible : Visibility.Collapsed;
            RenderTray();
            RenderSide();
        }

        /// <summary>切到中译英时，把对话专用的东西藏起来</summary>
        void MindHideChrome()
        {
            MindClosePopup();
            AttachTray.Visibility = Visibility.Collapsed;
            ReplyCard.Visibility = Visibility.Collapsed;
            Crumb.Visibility = Visibility.Collapsed;
            Side.Visibility = Visibility.Collapsed;
        }

        /// <summary>开始一段新对话（旧的留在历史里）</summary>
        void MindNew()
        {
            _session = null;
            _nodeId = null;
            _mindLoaded = true;
            ClearReply();
            RenderMind();
            RenderSide();
            UpdateSendBtn();
            SetStatus(null);
            if (_mode == Mode.Chat) Input.Focus();
        }

        /// <summary>设置里换了保存位置：下次打开时从新位置载入</summary>
        public static void OnMindFolderChanged()
        {
            var bar = Current;
            if (bar == null) return;
            bar._session = null;
            bar._nodeId = null;
            bar._mindLoaded = false;
            bar.ClearReply();
            if (bar.IsVisible && bar._mode == Mode.Chat) bar.MindOnShow();
        }

        /// <summary>Esc：先关追问小窗、取消追问，再收起窗口</summary>
        bool MindEscape()
        {
            if (_selPop != null && _selPop.IsOpen) { _selPop.IsOpen = false; return true; }
            if (_mode == Mode.Chat && _replyAnchor != null) { ClearReply(); return true; }
            return false;
        }

        void MindClosePopup()
        {
            if (_selPop != null) _selPop.IsOpen = false;
        }

        string MindPlaceholder()
        {
            if (_replyAnchor != null) return "就划线的内容追问，回车发送（直接回车 = " + Mind.Truncate(App.Settings.MindDefaultFollowUp, 12) + "）";
            if (_pending.Count > 0) return "要问这张图什么？回车发送（直接回车 = 分析图片）";
            var n = CurNode;
            if (n == null) return "问点什么，回车发送 · Ctrl+V 粘贴截图";
            if (n.Anchor != null) return "在这条追问里接着问，回车发送";
            return "接着问，回车发送 · 选中回答里的文字可以追问";
        }

        // ================= 提问 =================

        void MindSubmit(string text)
        {
            if (_replyAnchor == null && text.Length == 0 && _pending.Count == 0) return;
            if (_replyAnchor == null && MindCurrentRunning)
            {
                SetStatus("上一个回答还在写，写完再接着问（点 ■ 可以停止）。想追问上面的内容，选中那段文字就行。");
                return;
            }
            Input.Text = "";
            var images = TakePending();

            if (_replyAnchor != null && _session != null && CurNode != null)
            {
                var a = _replyAnchor;
                ClearReply();
                Branch(a, text, images);
                return;
            }
            ClearReply();
            if (text.Length == 0 && images == null) return;

            if (_session == null)
            {
                _session = Mind.NewSession(text.Length > 0 ? text : "图片提问");
                _nodeId = _session.RootId;
                _store.Add(_session);
            }
            var node = CurNode;
            if (node == null) { _nodeId = _session.RootId; node = CurNode; }
            _ = Ask(_session, node, text, images);
        }

        /// <summary>从选中的一段话分出一条追问，并切到它</summary>
        void Branch(MindAnchor anchor, string question, List<MindAttachment> images)
        {
            var s = _session;
            var parent = CurNode;
            if (s == null || parent == null) return;
            var q = (question ?? "").Trim();
            var child = new MindNode
            {
                Id = Mind.Uid(),
                ParentId = parent.Id,
                Anchor = anchor,
                Title = q.Length > 0 ? Mind.Truncate(q, 40) : $"「{Mind.Truncate(anchor.Text, 18)}」是什么意思？",
                CreatedAt = Mind.Now(),
            };
            s.Nodes[child.Id] = child;
            _nodeId = child.Id;
            _ = Ask(s, child, q.Length > 0 ? q : App.Settings.MindDefaultFollowUp, images);
            FocusInput();
        }

        void FocusInput()
        {
            if (_mode != Mode.Chat || !IsVisible) return;
            Dispatcher.BeginInvoke(new Action(() => { Input.Focus(); Keyboard.Focus(Input); }), DispatcherPriority.Input);
        }

        async Task Ask(MindSession s, MindNode node, string question, List<MindAttachment> images)
        {
            try { await AskInner(s, node, question, images); }
            catch (Exception e) { SetStatus("出错了：" + e.Message, true); UpdateSendBtn(); }
        }

        async Task AskInner(MindSession s, MindNode node, string question, List<MindAttachment> images)
        {
            if (string.IsNullOrWhiteSpace(question))
                question = images != null ? "请分析这张图片。" : App.Settings.MindDefaultFollowUp;
            var now = Mind.Now();
            var um = new MindMsg { Id = Mind.Uid(), Role = "user", Content = question, CreatedAt = now, Attachments = images };
            node.Messages.Add(um);
            if (string.IsNullOrEmpty(node.Title)) node.Title = Mind.Truncate(question, 40);
            if (string.IsNullOrEmpty(s.Title) && node.Id == s.RootId) s.Title = node.Title;
            var am = new MindMsg { Id = Mind.Uid(), Role = "assistant", Content = "", CreatedAt = now + 1, Pending = true };
            node.Messages.Add(am);
            _store.Save(s);

            var msgs = Mind.BuildThread(s, node.Id, LoadImageB64);
            if (IsCurrent(s, node)) RenderMind(toEnd: true);
            RenderSide();
            UpdatePlaceholder();
            await RunAnswer(s, node, am, msgs);
        }

        /// <summary>流式生成一条回答。可以同时有好几条在生成。</summary>
        async Task RunAnswer(MindSession s, MindNode node, MindMsg am, List<ChatMsg> msgs)
        {
            var r = new Running { Client = new AiClient(), S = s, Node = node, Msg = am };
            _running[am.Id] = r;
            am.Pending = true;
            UpdateSendBtn();
            MarkDirty(am);
            if (IsCurrent(s, node)) SetStatus("思考中…");

            var acc = new StringBuilder();
            List<SearchHit> sources = null;
            WebCallbacks web = null;
            if (WebAvailable && App.Settings.WebSearch)
            {
                web = new WebCallbacks
                {
                    OnSearch = q =>
                    {
                        r.Status = "正在搜索：" + q + " …";
                        MarkDirty(am);
                        if (IsCurrent(s, node)) SetStatus("正在联网搜索：" + q);
                    },
                    OnReset = () => { acc.Clear(); am.Content = ""; MarkDirty(am); },
                    OnSources = list => sources = list,
                };
            }
            var model = App.Settings.EffectiveModel;
            if (msgs.Any(m => m.HasImages) && App.Settings.Provider == Providers.DeepSeek) model = "deepseek-flash";
            am.Model = model;

            try
            {
                var full = await r.Client.StreamAsync(App.Settings, App.Settings.ChatSystem, msgs,
                    piece =>
                    {
                        acc.Append(piece);
                        am.Content = acc.ToString();
                        r.Status = null;
                        MarkDirty(am);
                        if (IsCurrent(s, node)) SetStatus("正在回答…");
                    },
                    onThinking: n =>
                    {
                        if (acc.Length > 0) return;
                        r.Status = $"深度思考中…（已想 {n} 字）";
                        MarkDirty(am);
                    },
                    web: web);
                am.Content = string.IsNullOrWhiteSpace(full) ? "（AI 返回为空）" : full;
                if (sources != null && sources.Count > 0)
                    am.Sources = sources.Select(h => new MindSource { Index = h.Index, Title = h.Title, Url = h.Url, Site = h.Site, Date = h.Date }).ToList();
                if (IsCurrent(s, node)) SetStatus(null);
            }
            catch (OperationCanceledException)
            {
                am.Content = acc.ToString();
                am.Content += (am.Content.Length > 0 ? "\n\n" : "") + "*（已停止生成）*";
            }
            catch (Exception e)
            {
                var msg = e is AiException ? e.Message : "出错了：" + e.Message;
                am.Content = (acc.Length > 0 ? acc + "\n\n" : "") + "❌ 请求失败：" + msg;
                if (IsCurrent(s, node)) SetStatus(msg, true);
            }
            finally
            {
                _running.Remove(am.Id);
                am.Pending = false;
                am.DoneAt = Mind.Now();
                if (s.Nodes.ContainsKey(node.Id)) _store.Save(s, true);
                _dirty.Remove(am.Id);
                if (_views.TryGetValue(am.Id, out var v)) UpdateView(v);
                UpdateSendBtn();
                if (_sideOpen && _sideTab == "tree" && s == _session) RenderSide();
                UpdatePlaceholder();
            }
        }

        void MindStopCurrent()
        {
            var n = CurNode;
            foreach (var r in _running.Values.ToList())
                if (r.S == _session && r.Node == n) r.Client.Cancel();
        }

        /// <summary>重新回答：用这条回答之前的对话重新生成</summary>
        void Regenerate(MindNode node, MindMsg m)
        {
            var s = _session;
            if (s == null || _running.ContainsKey(m.Id)) return;
            if (AnchorsFor(node, m).Count > 0 &&
                MessageBox.Show(this, "这条回答下面有划线追问。重新回答后，原来划线的句子可能对不上，高亮会消失（追问本身还在问答树里）。继续？",
                    "划词助手", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            m.Content = "";
            m.Sources = null;
            m.Extra?.Remove("editedAt");
            m.Extra?.Remove("originalContent");
            m.CreatedAt = Mind.Now();
            m.Pending = true;
            var msgs = Mind.BuildThread(s, node.Id, LoadImageB64, uptoMsgId: m.Id);
            _ = RunSafe(s, node, m, msgs);
        }

        async Task RunSafe(MindSession s, MindNode node, MindMsg m, List<ChatMsg> msgs)
        {
            try { await RunAnswer(s, node, m, msgs); }
            catch (Exception e) { SetStatus("出错了：" + e.Message, true); }
        }

        /// <summary>退出程序时：还在写的回答把已写出的部分存下来</summary>
        public void MindShutdown()
        {
            foreach (var r in _running.Values.ToList())
            {
                try
                {
                    r.Client.Cancel();
                    r.Msg.Pending = false;
                    r.Msg.Content = (r.Msg.Content ?? "") + (string.IsNullOrEmpty(r.Msg.Content) ? "" : "\n\n") + "*（已停止生成）*";
                    r.Msg.DoneAt = Mind.Now();
                    _store.Save(r.S, true);
                }
                catch { }
            }
            _running.Clear();
            _store.Flush();
        }

        // ================= 划线追问 =================

        void OnAnswerSelect(RichTextBox rtb, MindNode node, MindMsg m)
        {
            if (_running.ContainsKey(m.Id)) return; // 还在写，等写完再追问
            if (rtb.Selection == null || rtb.Selection.IsEmpty) return;
            var text = MindRender.CleanSelection(rtb.Selection.Text);
            if (text.Length == 0) return;
            if (node.Id != _nodeId) return;
            _selAnchor = new MindAnchor { Text = text, MsgId = m.Id };
            ShowSelPop();
        }

        void ShowSelPop()
        {
            if (_selPop == null)
            {
                _selPopPanel = new WrapPanel { Margin = new Thickness(8, 8, 2, 2) };
                var border = new Border
                {
                    Background = App.Brush("Surface"),
                    BorderBrush = App.Brush("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    MaxWidth = 380,
                    Margin = new Thickness(10),
                    Child = _selPopPanel,
                    Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.3 },
                };
                TextElement.SetFontFamily(border, (FontFamily)FindResource("UiFont"));
                TextElement.SetFontSize(border, 13.0);
                _selPop = new Popup
                {
                    AllowsTransparency = true,
                    Placement = PlacementMode.MousePoint,
                    StaysOpen = false,
                    PopupAnimation = PopupAnimation.Fade,
                    HorizontalOffset = -6,
                    VerticalOffset = 6,
                    PlacementTarget = this,
                    Child = border,
                };
            }

            _selPopPanel.Children.Clear();
            var chip = (Style)FindResource("ChipBtn");
            var ask = new Button
            {
                Content = "✦ 追问…",
                Style = chip,
                Background = App.Brush("Accent"),
                Foreground = App.Brush("AccentInk"),
                FontWeight = FontWeights.SemiBold,
                ToolTip = "就这段话提问（在输入框里写问题）",
            };
            ask.Click += (s, e) => StartReply(_selAnchor);
            _selPopPanel.Children.Add(ask);
            foreach (var p in App.Settings.MindPresetList())
            {
                var q = p;
                var b = new Button { Content = Mind.Truncate(q, 14), Style = chip, ToolTip = q };
                b.Click += (s, e) =>
                {
                    var a = _selAnchor;
                    MindClosePopup();
                    if (a == null) return;
                    ClearReply();
                    Branch(a, q, TakePending());
                };
                _selPopPanel.Children.Add(b);
            }
            var copy = new Button { Content = "复制", Style = chip };
            copy.Click += (s, e) => { MindClosePopup(); if (_selAnchor != null) CopyText(_selAnchor.Text); };
            _selPopPanel.Children.Add(copy);

            _selPop.IsOpen = false;
            _selPop.IsOpen = true;
        }

        /// <summary>「追问…」：划线原文挂在输入框上方，在输入框里写问题</summary>
        void StartReply(MindAnchor a)
        {
            MindClosePopup();
            if (a == null) return;
            _replyAnchor = a;
            ReplyText.Text = a.Text;
            ReplyCard.Visibility = Visibility.Visible;
            UpdatePlaceholder();
            Activate();
            Input.Focus();
            Keyboard.Focus(Input);
        }

        void ClearReply()
        {
            _replyAnchor = null;
            ReplyCard.Visibility = Visibility.Collapsed;
            UpdatePlaceholder();
        }

        // ================= 导航 =================

        void OpenNode(string nodeId, string focusMsgId = null)
        {
            if (_session == null || !_session.Nodes.ContainsKey(nodeId)) return;
            _nodeId = nodeId;
            ClearReply();
            MindClosePopup();
            RenderMind(focusMsgId: focusMsgId, toEnd: focusMsgId == null);
            RenderSide();
            UpdateSendBtn();
            UpdatePlaceholder();
            SetStatus(MindCurrentRunning ? "正在回答…" : null);
            FocusInput();
        }

        void OpenSession(MindSession s)
        {
            _session = s;
            _nodeId = s.RootId;
            ClearReply();
            RenderMind(toEnd: true);
            RenderSide();
            UpdateSendBtn();
            UpdatePlaceholder();
            SetStatus(null);
            FocusInput();
        }

        /// <summary>回到分出这条追问的地方，并滚到那条回答</summary>
        void GoUp()
        {
            var n = CurNode;
            if (n?.ParentId == null) return;
            OpenNode(n.ParentId, n.Anchor?.MsgId);
        }

        void UpdateCrumb()
        {
            var n = CurNode;
            if (_mode != Mode.Chat || n == null || n.ParentId == null)
            {
                Crumb.Visibility = Visibility.Collapsed;
                return;
            }
            var tree = Mind.Tree(_session);
            string Label(MindNode x)
            {
                foreach (var it in tree) if (it.Node == x && it.FirstOfNode) return it.Label;
                return "";
            }
            var sb = new StringBuilder("主线");
            foreach (var x in Mind.ChainOf(_session, n.Id).Skip(1))
                sb.Append("  ›  ").Append(Label(x)).Append(' ').Append(Mind.Truncate(x.Title, 16));
            CrumbText.Text = sb.ToString();
            CrumbText.ToolTip = CrumbText.Text;
            Crumb.Visibility = Visibility.Visible;
        }

        // ================= 主窗口：对话 =================

        void RenderMind(string focusMsgId = null, bool toEnd = false)
        {
            ChatPanel.Children.Clear();
            _views.Clear();
            _userViews.Clear();
            _dirty.Clear();
            var node = CurNode;
            UpdateCrumb();

            if (node != null)
            {
                if (node.Anchor != null && !string.IsNullOrEmpty(node.Anchor.Text)) AddAnchorQuote(node);
                foreach (var m in node.Messages)
                {
                    if (m.Role == "user") AddUserView(m);
                    else AddAnswerView(node, m);
                }
            }
            if (ChatPanel.Children.Count == 0 && _sideOpen)
            {
                ChatPanel.Children.Add(new TextBlock
                {
                    Text = "问点什么，开始一段对话。\n\n在 AI 的回答里选中一段看不懂的文字，就能就这段追问：追问在单独的分支里展开，主回答不会被淹没；所有追问会排进左边的问答树。\n\nCtrl+V 可以粘贴截图。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = App.Brush("InkSoft"),
                    FontSize = 13,
                    Margin = new Thickness(6, 8, 6, 8),
                });
            }
            UpdateOutputVisibility();

            if (focusMsgId != null) ScrollToMsg(focusMsgId);
            else if (toEnd) ScrollToEnd();
        }

        /// <summary>分支最上面：划线的原文，点它回到原处</summary>
        void AddAnchorQuote(MindNode node)
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock
            {
                Text = "划线原文 · 点击回到原处",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = App.Brush("Accent"),
            });
            box.Children.Add(new TextBlock
            {
                Text = node.Anchor.Text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 80,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = App.Brush("InkSoft"),
                Margin = new Thickness(0, 3, 0, 0),
            });
            var quote = new Border
            {
                Child = box,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 7, 12, 7),
                Background = App.Brush("Quote"),
                BorderBrush = App.Brush("MarkStrong"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            quote.MouseLeftButtonUp += (s, e) => GoUp();
            ChatPanel.Children.Add(quote);
        }

        void AddUserView(MindMsg m)
        {
            var stack = new StackPanel();
            var imgs = m.Images.ToList();
            int otherFiles = (m.Attachments?.Count ?? 0) - imgs.Count;
            if (imgs.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                foreach (var a in imgs)
                {
                    var path = _store.Resolve(a);
                    if (path != null && File.Exists(path))
                    {
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.DecodePixelHeight = 160;
                            bi.UriSource = new Uri(path);
                            bi.EndInit();
                            bi.Freeze();
                            var img = new Image
                            {
                                Source = bi, Height = 80, Stretch = Stretch.Uniform,
                                Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = "点击打开原图",
                            };
                            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                            var p = path;
                            img.MouseLeftButtonUp += (s, e) =>
                            {
                                try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
                            };
                            wrap.Children.Add(img);
                            continue;
                        }
                        catch { }
                    }
                    wrap.Children.Add(new TextBlock { Text = "🖼 图片不见了", FontSize = 12, Foreground = App.Brush("InkSoft"), Margin = new Thickness(0, 0, 8, 0) });
                }
                stack.Children.Add(wrap);
            }
            if (otherFiles > 0)
                stack.Children.Add(new TextBlock { Text = $"📎 {otherFiles} 个文件", FontSize = 12, Foreground = App.Brush("InkSoft") });

            var box = new TextBox
            {
                Text = m.Content,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = App.Brush("Ink"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5,
                Padding = new Thickness(0),
                SelectionBrush = App.Brush("Accent"),
            };
            ForwardWheel(box);
            stack.Children.Add(box);

            var bubble = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(60, ChatPanel.Children.Count == 0 ? 0 : 12, 0, 0),
                Background = App.Brush("Surface2"),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            ChatPanel.Children.Add(bubble);
            _userViews[m.Id] = bubble;
        }

        void AddAnswerView(MindNode node, MindMsg m)
        {
            var box = new StackPanel { Margin = new Thickness(0, ChatPanel.Children.Count == 0 ? 0 : 10, 0, 0) };
            var status = new TextBlock
            {
                Text = "思考中…",
                FontSize = 13.5,
                Foreground = App.Brush("InkSoft"),
                Margin = new Thickness(4, 2, 0, 2),
                Visibility = Visibility.Collapsed,
            };
            var rtb = new RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                IsUndoEnabled = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = App.Brush("Ink"),
                Padding = new Thickness(0),
                Margin = new Thickness(4, 2, 4, 0),
                SelectionBrush = App.Brush("Accent"),
                FontSize = AnswerFont,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Cursor = Cursors.IBeam,
            };
            ForwardWheel(rtb);
            rtb.PreviewMouseLeftButtonUp += (s, e) =>
                Dispatcher.BeginInvoke(new Action(() => OnAnswerSelect(rtb, node, m)), DispatcherPriority.Input);
            var extras = new StackPanel();
            box.Children.Add(status);
            box.Children.Add(rtb);
            box.Children.Add(extras);
            ChatPanel.Children.Add(box);

            var v = new MsgView { Node = node, Msg = m, Box = box, Status = status, Rtb = rtb, Extras = extras };
            _views[m.Id] = v;
            UpdateView(v);
        }

        void ForwardWheel(UIElement el)
        {
            // 只读文本框会吞掉滚轮：转给外面的滚动区
            el.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                OutScroll.ScrollToVerticalOffset(OutScroll.VerticalOffset - e.Delta / 3.0);
            };
        }

        List<MindRender.Anchor> AnchorsFor(MindNode node, MindMsg m)
        {
            var list = new List<MindRender.Anchor>();
            if (_session == null) return list;
            foreach (var c in Mind.ChildrenOf(_session, node.Id))
                if (c.Anchor != null && c.Anchor.MsgId == m.Id && !string.IsNullOrEmpty(c.Anchor.Text))
                    list.Add(new MindRender.Anchor { Text = c.Anchor.Text, ChildId = c.Id, Tip = "已追问：" + c.Title + "\n点击打开" });
            return list;
        }

        void UpdateView(MsgView v)
        {
            var m = v.Msg;
            bool running = _running.TryGetValue(m.Id, out var r);
            if (running && string.IsNullOrEmpty(m.Content))
            {
                v.Status.Text = r.Status ?? "思考中…";
                v.Status.Visibility = Visibility.Visible;
                v.Rtb.Visibility = Visibility.Collapsed;
            }
            else
            {
                v.Status.Visibility = Visibility.Collapsed;
                v.Rtb.Visibility = Visibility.Visible;
                var node = v.Node;
                var doc = MindRender.Build(m.Content, running ? null : AnchorsFor(node, m),
                    id => Dispatcher.BeginInvoke(new Action(() => OpenNode(id))), AnswerFont);
                if ((m.Content ?? "").StartsWith("❌")) doc.Foreground = App.Brush("Danger");
                v.Rtb.Document = doc;
            }

            v.Extras.Children.Clear();
            if (running) return;
            if (m.Sources != null && m.Sources.Count > 0) v.Extras.Children.Add(SourcesView(m.Sources));

            var bar = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            var tiny = (Style)FindResource("TinyBtn");
            var copy = new Button { Content = "复制", Style = tiny, ToolTip = "复制这条回答（Markdown）" };
            copy.Click += (s, e) => CopyText(m.Content);
            var redo = new Button { Content = "重新回答", Style = tiny };
            redo.Click += (s, e) => Regenerate(v.Node, m);
            bar.Children.Add(copy);
            bar.Children.Add(redo);
            var kids = AnchorsFor(v.Node, m).Count;
            var meta = Mind.FmtTime(m.DoneAt ?? m.CreatedAt);
            if (!string.IsNullOrEmpty(m.Model)) meta += " · " + m.Model;
            meta += $" · {MindRender.Plain(m.Content).Length} 字";
            if (kids > 0) meta += $" · {kids} 条追问";
            bar.Children.Add(new TextBlock
            {
                Text = meta,
                FontSize = 11.5,
                Foreground = App.Brush("InkSoft"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.8,
            });
            v.Extras.Children.Add(bar);
        }

        /// <summary>在回答下面列出引用的来源，点标题用浏览器打开</summary>
        FrameworkElement SourcesView(List<MindSource> sources)
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
                var meta = h.Site ?? "";
                if (!string.IsNullOrEmpty(h.Date) && h.Date.Length >= 10) meta = (meta.Length > 0 ? meta + " · " : "") + h.Date.Substring(0, 10);
                if (meta.Length > 0) tb.Inlines.Add(new Run("  " + meta));
                box.Children.Add(tb);
            }
            return box;
        }

        // ---- 流式更新：攒一下再刷新，避免每个字都重排 ----

        void MarkDirty(MindMsg m)
        {
            if (!_views.ContainsKey(m.Id)) return;
            _dirty.Add(m.Id);
            if (!_renderTimer.IsEnabled) _renderTimer.Start();
        }

        void FlushDirty()
        {
            _renderTimer.Stop();
            if (_dirty.Count == 0) return;
            bool atEnd = _mode == Mode.Chat && OutScroll.VerticalOffset >= OutScroll.ScrollableHeight - 40;
            foreach (var id in _dirty.ToList())
                if (_views.TryGetValue(id, out var v)) UpdateView(v);
            _dirty.Clear();
            if (atEnd) ScrollToEnd();
        }

        void ScrollToEnd()
        {
            if (_mode != Mode.Chat) return;
            Dispatcher.BeginInvoke(new Action(() => OutScroll.ScrollToEnd()), DispatcherPriority.Background);
        }

        void ScrollToMsg(string msgId)
        {
            FrameworkElement el = _userViews.TryGetValue(msgId, out var u) ? u
                : _views.TryGetValue(msgId, out var v) ? v.Box : null;
            if (el == null) { ScrollToEnd(); return; }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    OutScroll.UpdateLayout();
                    var pt = el.TranslatePoint(new Point(0, 0), ChatPanel);
                    OutScroll.ScrollToVerticalOffset(Math.Max(0, pt.Y - 6));
                }
                catch { }
            }), DispatcherPriority.Background);
        }

        // ================= 侧栏：问答树 / 历史 =================

        void ToggleSide(string tab)
        {
            if (tab == null || (_sideOpen && _sideTab == tab)) _sideOpen = false;
            else { _sideOpen = true; _sideTab = tab; }
            UpdateLayoutForMode();
            RenderSide();
            // 空对话时主窗口里放一段说明
            if (CurNode == null || CurNode.Messages.Count == 0) RenderMind();
            UpdateOutputVisibility();
            Input.Focus();
        }

        void StyleTab(Button b, bool on)
        {
            b.Background = on ? App.Brush("AccentSoft") : Brushes.Transparent;
            b.Foreground = App.Brush(on ? "Accent" : "InkSoft");
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void RenderSide()
        {
            StyleTab(TreeBtn, _sideOpen && _sideTab == "tree");
            StyleTab(HistBtn, _sideOpen && _sideTab == "hist");
            if (!_sideOpen || _mode != Mode.Chat)
            {
                Side.Visibility = Visibility.Collapsed;
                return;
            }
            Side.Visibility = Visibility.Visible;
            StyleTab(TabTree, _sideTab == "tree");
            StyleTab(TabHist, _sideTab == "hist");
            HistSearchBox.Visibility = _sideTab == "hist" ? Visibility.Visible : Visibility.Collapsed;
            SideList.Children.Clear();
            if (_sideTab == "hist") RenderHistory();
            else RenderTree();
        }

        Border SideRow(UIElement content, bool current, double indent, string tip)
        {
            var row = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6 + indent, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 1),
                Background = current ? App.Brush("AccentSoft") : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tip,
            };
            if (!current)
            {
                row.MouseEnter += (s, e) => row.Background = App.Brush("Hover");
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            }
            return row;
        }

        void SideEmpty(string text)
        {
            SideList.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = App.Brush("InkSoft"),
                Margin = new Thickness(8, 8, 8, 8),
            });
        }

        void RenderTree()
        {
            SideHint.Text = "在回答里选中一段文字就能追问 · 黄色高亮 = 已追问过的句子 · 右键分支可删除";
            var s = _session;
            if (s == null || s.Root == null || s.Root.Messages.Count == 0)
            {
                SideEmpty("还没有对话。问第一个问题后，这里会列出每一问，以及每一次划线追问分出来的分支。");
                return;
            }
            var path = new HashSet<string>(Mind.ChainOf(s, _nodeId).Select(n => n.Id));
            var items = Mind.Tree(s);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                bool cur = it.Node.Id == _nodeId;
                bool lastOfNode = items.Skip(i + 1).All(x => x.Node != it.Node);
                var dock = new DockPanel();
                var num = new TextBlock
                {
                    Text = it.Label,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = App.Brush(cur || path.Contains(it.Node.Id) ? "Accent" : "InkSoft"),
                    Margin = new Thickness(0, 1, 6, 0),
                    MinWidth = 18,
                };
                DockPanel.SetDock(num, Dock.Left);
                dock.Children.Add(num);

                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 38,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12.5,
                    Foreground = App.Brush("Ink"),
                    LineHeight = 18,
                };
                if (it.FirstOfNode && it.Node.Anchor != null && !string.IsNullOrEmpty(it.Node.Anchor.Text))
                {
                    tb.Inlines.Add(new Run(Mind.Truncate(it.Node.Anchor.Text, 12)) { Background = App.Brush("Mark") });
                    tb.Inlines.Add(new Run(" "));
                }
                var q = it.User?.Content ?? it.Node.Title ?? "未命名问题";
                tb.Inlines.Add(new Run(Mind.Truncate(q, 60)) { FontWeight = cur ? FontWeights.SemiBold : FontWeights.Normal });
                bool running = _running.Values.Any(r => r.S == s && r.Node == it.Node);
                if (running && lastOfNode) tb.Inlines.Add(new Run("  …") { Foreground = App.Brush("Accent") });
                dock.Children.Add(tb);

                var tip = (it.FirstOfNode && it.Node.Anchor != null ? "划线「" + Mind.Truncate(it.Node.Anchor.Text, 80) + "」\n" : "") + Mind.Truncate(q, 200);
                var row = SideRow(dock, cur, it.Depth * 14, tip);
                var item = it;
                row.MouseLeftButtonUp += (o, e) => OpenNode(item.Node.Id, item.User?.Id);
                if (item.FirstOfNode && item.Node.ParentId != null)
                {
                    var menu = new ContextMenu();
                    var del = new MenuItem { Header = "删除这条追问（连同下面的分支）" };
                    del.Click += (o, e) => DeleteNode(item.Node);
                    menu.Items.Add(del);
                    row.ContextMenu = menu;
                }
                SideList.Children.Add(row);
            }
        }

        void RenderHistory()
        {
            var q = HistSearch.Text.Trim();
            var all = _store.List();
            var list = all.Where(s => q.Length == 0 || Matches(s, q)).ToList();
            SideHint.Text = $"共 {all.Count} 段对话 · 右键可删除 · 保存位置在设置里改";
            if (list.Count == 0)
            {
                SideEmpty(all.Count == 0 ? "还没有历史对话。" : "没有找到包含「" + q + "」的对话。");
                return;
            }
            foreach (var s in list)
            {
                bool cur = s == _session;
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(s.Title) ? "未命名对话" : s.Title,
                    FontSize = 12.5,
                    FontWeight = cur ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = App.Brush("Ink"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                int branches = s.Nodes.Count - 1;
                var meta = Mind.FmtTime(s.UpdatedAt) + $" · {Mind.QuestionCount(s)} 问";
                if (branches > 0) meta += $" · {branches} 条追问";
                if (_running.Values.Any(r => r.S == s)) meta += " · 回答中…";
                stack.Children.Add(new TextBlock { Text = meta, FontSize = 11, Foreground = App.Brush("InkSoft"), Margin = new Thickness(0, 1, 0, 0) });

                var row = SideRow(stack, cur, 0, s.Title);
                var sess = s;
                row.MouseLeftButtonUp += (o, e) => OpenSession(sess);
                var menu = new ContextMenu();
                var del = new MenuItem { Header = "删除这段对话" };
                del.Click += (o, e) => DeleteSession(sess);
                menu.Items.Add(del);
                row.ContextMenu = menu;
                SideList.Children.Add(row);
            }
        }

        static bool Matches(MindSession s, string q)
        {
            if ((s.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var n in s.Nodes.Values)
                foreach (var m in n.Messages)
                    if ((m.Content ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void DeleteNode(MindNode node)
        {
            var s = _session;
            if (s == null || node.ParentId == null) return;
            var ids = Mind.SubtreeIds(s, node.Id);
            var more = ids.Count > 1 ? $"和它下面的 {ids.Count - 1} 条分支" : "";
            if (MessageBox.Show(this, $"删除追问「{Mind.Truncate(node.Title, 30)}」{more}？", "划词助手",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            foreach (var r in _running.Values.ToList())
                if (r.S == s && ids.Contains(r.Node.Id)) r.Client.Cancel();
            foreach (var id in ids) s.Nodes.Remove(id);
            ClearReply();
            MindClosePopup();
            if (_nodeId != null && ids.Contains(_nodeId)) _nodeId = node.ParentId;
            _store.Save(s, true);
            RenderMind();
            RenderSide();
            UpdateSendBtn();
            UpdatePlaceholder();
        }

        void DeleteSession(MindSession s)
        {
            if (MessageBox.Show(this, $"删除对话「{Mind.Truncate(s.Title, 30)}」？删除后不能恢复。", "划词助手",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            foreach (var r in _running.Values.ToList())
                if (r.S == s) r.Client.Cancel();
            _store.Delete(s.Id);
            if (s == _session) { _session = null; _nodeId = null; ClearReply(); RenderMind(); }
            RenderSide();
            UpdateSendBtn();
            UpdatePlaceholder();
        }

        // ================= 图片 =================

        bool TryPasteImage()
        {
            try
            {
                // 剪贴板里有文字就照常粘贴文字
                if (Clipboard.ContainsText()) return false;
            }
            catch { }
            var img = ReadClipboardImage();
            if (img == null) return false;
            AddPending(img);
            return true;
        }

        void PickImages()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择图片",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames)
            {
                try { AddPending(LoadImageFile(f)); }
                catch { SetStatus("这张图片打不开：" + Path.GetFileName(f), true); }
            }
            Activate();
            Input.Focus();
        }

        void AddPending(BitmapSource img)
        {
            if (_pending.Count >= MaxImages) { SetStatus($"一次最多 {MaxImages} 张图片", true); return; }
            byte[] jpg = null;
            try { jpg = EncodeJpeg(img); } catch { }
            if (jpg == null) { SetStatus("图片读不出来，换一张试试", true); return; }
            _pending.Add((img, jpg));
            RenderTray();
            UpdatePlaceholder();
            SetStatus("已添加图片，写上问题回车发送；直接回车 = 分析图片。看图需要能看图的模型。");
        }

        void RenderTray()
        {
            AttachTray.Children.Clear();
            for (int i = 0; i < _pending.Count; i++)
            {
                int idx = i;
                var img = new Image { Source = _pending[i].thumb, Height = 60, MaxWidth = 140, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                var x = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("TinyBtn"),
                    Height = 20,
                    Padding = new Thickness(5, 0, 5, 0),
                    Background = App.Brush("Surface"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 2, 0),
                    ToolTip = "去掉这张",
                };
                x.Click += (s, e) =>
                {
                    if (idx < _pending.Count) _pending.RemoveAt(idx);
                    RenderTray();
                    UpdatePlaceholder();
                };
                var g = new Grid();
                g.Children.Add(new Border { Child = img, Padding = new Thickness(3), CornerRadius = new CornerRadius(8), Background = App.Brush("Surface2") });
                g.Children.Add(x);
                g.Margin = new Thickness(0, 0, 8, 0);
                AttachTray.Children.Add(g);
            }
            AttachTray.Visibility = _pending.Count > 0 && _mode == Mode.Chat ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>把待发送的图片存进附件文件夹，变成附件记录</summary>
        List<MindAttachment> TakePending()
        {
            if (_pending.Count == 0) return null;
            var list = new List<MindAttachment>();
            foreach (var p in _pending)
            {
                try
                {
                    var a = _store.SaveImage(p.jpeg, "粘贴的图片");
                    var full = _store.Resolve(a);
                    if (full != null) ImgCache[full] = Convert.ToBase64String(p.jpeg);
                    list.Add(a);
                }
                catch (Exception e) { SetStatus("图片保存失败：" + e.Message, true); }
            }
            _pending.Clear();
            RenderTray();
            return list.Count > 0 ? list : null;
        }

        /// <summary>附件 → 发给 AI 的 JPEG base64（插件存的 png 等也统一转成 JPEG）</summary>
        string LoadImageB64(MindAttachment a)
        {
            var path = _store.Resolve(a);
            if (path == null) return null;
            if (ImgCache.TryGetValue(path, out var hit)) return hit;
            if (!File.Exists(path)) return null;
            try
            {
                string b64;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var bytes = File.ReadAllBytes(path);
                if ((ext == ".jpg" || ext == ".jpeg") && bytes.Length < 4_000_000) b64 = Convert.ToBase64String(bytes);
                else
                {
                    var jpg = EncodeJpeg(LoadImageFile(path));
                    if (jpg == null) return null;
                    b64 = Convert.ToBase64String(jpg);
                }
                ImgCache[path] = b64;
                return b64;
            }
            catch { return null; }
        }
    }
}
