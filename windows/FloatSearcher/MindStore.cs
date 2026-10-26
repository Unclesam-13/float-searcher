using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace FloatSearcher
{
    // ============================================================
    // 追问树的数据：和 Obsidian 插件 Canvas Mindflow 的「对话数据/*.json」格式一致，
    // 把保存位置设成库里的 Mindflow\对话数据，两边就能共用历史。
    // 不认识的字段（附件、编辑记录等）原样保留，不会被这里改丢。
    // ============================================================

    /// <summary>一个对话：一棵问答树</summary>
    public class MindSession
    {
        public string Id { get; set; }
        public int Version { get; set; } = 1;
        public string Title { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public string RootId { get; set; }
        public Dictionary<string, MindNode> Nodes { get; set; } = new Dictionary<string, MindNode>();
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }

        [JsonIgnore] public MindNode Root => RootId != null && Nodes.TryGetValue(RootId, out var n) ? n : null;
    }

    /// <summary>树上的一个「窗口」：主线，或从某个回答里划线追问出来的分支</summary>
    public class MindNode
    {
        public string Id { get; set; }
        public string ParentId { get; set; }
        /// <summary>从父窗口哪条回答的哪段话追问出来的；主线为空</summary>
        public MindAnchor Anchor { get; set; }
        public string Title { get; set; }
        public long CreatedAt { get; set; }
        public List<MindMsg> Messages { get; set; } = new List<MindMsg>();
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    public class MindAnchor
    {
        public string Text { get; set; }
        public string MsgId { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    public class MindMsg
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Content { get; set; } = "";
        public long CreatedAt { get; set; }
        public string Model { get; set; }
        public long? DoneAt { get; set; }
        /// <summary>联网搜索引用的来源（只有这边会写）</summary>
        public List<MindSource> Sources { get; set; }
        /// <summary>提问附带的图片 / 文件（和插件一样只记路径，原件在「附件」文件夹）</summary>
        public List<MindAttachment> Attachments { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }

        /// <summary>正在生成（不保存）</summary>
        [JsonIgnore] public bool Pending { get; set; }

        [JsonIgnore] public IEnumerable<MindAttachment> Images =>
            Attachments == null ? Enumerable.Empty<MindAttachment>() : Attachments.Where(a => a.Kind == "image");
    }

    public class MindAttachment
    {
        public string Id { get; set; }
        /// <summary>image / pdf / text</summary>
        public string Kind { get; set; }
        public string Name { get; set; }
        /// <summary>相对路径，用 / 分隔（插件里是相对库根目录）</summary>
        public string Path { get; set; }
        public string Mime { get; set; }
        public long? Size { get; set; }
        /// <summary>插件提取出来的文件文字</summary>
        public string Text { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }
    }

    public class MindSource
    {
        public int Index { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string Site { get; set; }
        public string Date { get; set; }
    }

    /// <summary>问答树上的一行（已经算好编号和缩进）</summary>
    public class MindTreeItem
    {
        public string Label;       // 1、1.1、1.1.2 …
        public int Depth;
        public MindNode Node;
        public MindMsg User;       // 这一轮的提问（可能为空：窗口还没提问）
        public bool FirstOfNode;   // 窗口里的第一问（分支的入口，显示划线原文）
    }

    public static class Mind
    {
        static readonly Random Rng = new Random();

        public static long Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

        /// <summary>和插件一样的 id：时间戳 36 进制 + 6 位随机</summary>
        public static string Uid()
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            string B36(long v)
            {
                if (v == 0) return "0";
                var sb = new StringBuilder();
                while (v > 0) { sb.Insert(0, digits[(int)(v % 36)]); v /= 36; }
                return sb.ToString();
            }
            var r = new StringBuilder();
            lock (Rng) for (int i = 0; i < 6; i++) r.Append(digits[Rng.Next(36)]);
            return B36(Now()) + r;
        }

        public static string Truncate(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        public static string FmtTime(long ts)
        {
            var d = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
            var now = DateTime.Now;
            var hm = d.ToString("HH:mm");
            if (d.Date == now.Date) return "今天 " + hm;
            if (d.Date == now.Date.AddDays(-1)) return "昨天 " + hm;
            if (d.Year == now.Year) return $"{d.Month}月{d.Day}日 {hm}";
            return $"{d.Year}年{d.Month}月{d.Day}日";
        }

        public static MindSession NewSession(string question)
        {
            var now = Now();
            var root = new MindNode { Id = Uid(), Title = Truncate(question, 40), CreatedAt = now };
            return new MindSession
            {
                Id = Uid(), Title = root.Title, CreatedAt = now, UpdatedAt = now,
                RootId = root.Id, Nodes = new Dictionary<string, MindNode> { [root.Id] = root },
            };
        }

        // ---------------- 树 ----------------

        /// <summary>从主线到这个窗口的一串窗口（主线在前）</summary>
        public static List<MindNode> ChainOf(MindSession s, string nodeId)
        {
            var list = new List<MindNode>();
            var seen = new HashSet<string>();
            MindNode n = s != null && nodeId != null && s.Nodes.TryGetValue(nodeId, out var x) ? x : null;
            while (n != null && seen.Add(n.Id))
            {
                list.Insert(0, n);
                n = n.ParentId != null && s.Nodes.TryGetValue(n.ParentId, out var p) ? p : null;
            }
            return list;
        }

        public static List<MindNode> ChildrenOf(MindSession s, string id) =>
            s.Nodes.Values.Where(n => n.ParentId == id).OrderBy(n => n.CreatedAt).ToList();

        public static List<string> SubtreeIds(MindSession s, string id)
        {
            var list = new List<string> { id };
            foreach (var c in ChildrenOf(s, id)) list.AddRange(SubtreeIds(s, c.Id));
            return list;
        }

        /// <summary>一个窗口里的「一问一答」</summary>
        public static List<(MindMsg user, List<string> ids)> RoundsOf(MindNode node)
        {
            var list = new List<(MindMsg, List<string>)>();
            (MindMsg, List<string>)? cur = null;
            foreach (var m in node.Messages)
            {
                if (m.Role == "user")
                {
                    var r = (m, new List<string> { m.Id });
                    list.Add(r);
                    cur = r;
                }
                else if (cur != null)
                {
                    cur.Value.Item2.Add(m.Id);
                    cur = null;
                }
                else list.Add((null, new List<string> { m.Id }));
            }
            if (list.Count == 0) list.Add((null, new List<string>()));
            return list;
        }

        /// <summary>
        /// 问答树：同一个窗口里接着问的是同级（1、2、3…）；
        /// 从某个回答里划线追问出来的窗口挂在那一问下面（1.1、1.2…）。和插件的编号一致。
        /// </summary>
        public static List<MindTreeItem> Tree(MindSession s)
        {
            var list = new List<MindTreeItem>();
            var root = s?.Root;
            if (root == null) return list;
            var seen = new HashSet<string>();

            int Walk(MindNode node, string prefix, int start, int depth)
            {
                if (!seen.Add(node.Id)) return 0;
                var rounds = RoundsOf(node);
                var kidsBy = new Dictionary<int, List<MindNode>>();
                foreach (var c in ChildrenOf(s, node.Id))
                {
                    int idx = c.Anchor != null ? rounds.FindIndex(r => r.ids.Contains(c.Anchor.MsgId)) : -1;
                    if (idx < 0) idx = rounds.Count - 1;
                    if (!kidsBy.TryGetValue(idx, out var l)) kidsBy[idx] = l = new List<MindNode>();
                    l.Add(c);
                }
                for (int i = 0; i < rounds.Count; i++)
                {
                    var label = prefix + (start + i + 1);
                    list.Add(new MindTreeItem { Label = label, Depth = depth, Node = node, User = rounds[i].user, FirstOfNode = i == 0 });
                    int k = 0;
                    if (kidsBy.TryGetValue(i, out var kids))
                        foreach (var c in kids) k += Walk(c, label + ".", k, depth + 1);
                }
                return rounds.Count;
            }
            Walk(root, "", 0, 0);
            return list;
        }

        /// <summary>这个窗口在树上的编号（第一问的编号）</summary>
        public static string LabelOf(MindSession s, string nodeId)
        {
            foreach (var it in Tree(s)) if (it.Node.Id == nodeId && it.FirstOfNode) return it.Label;
            return "";
        }

        public static int QuestionCount(MindSession s) =>
            s.Nodes.Values.Sum(n => n.Messages.Count(m => m.Role == "user"));

        // ---------------- 发给 AI 的上下文 ----------------

        const int FullDepth = 3;       // 离当前窗口 3 层以内的回答发全文
        const int TruncateChars = 600; // 更远的只发开头

        /// <summary>
        /// 祖先窗口的对话（到分出当前分支的那条回答为止，远的截断）+ 当前窗口的对话。
        /// 分支的第一问前面会带上划线的原文。uptoMsgId：只取到这条消息之前（重新回答时用）。
        /// </summary>
        public static List<ChatMsg> BuildThread(MindSession s, string nodeId, Func<MindAttachment, string> loadImage,
                                                string uptoMsgId = null)
        {
            var chain = ChainOf(s, nodeId);
            var msgs = new List<ChatMsg>();
            for (int idx = 0; idx < chain.Count; idx++)
            {
                var node = chain[idx];
                int dist = chain.Count - 1 - idx;
                bool isCur = idx == chain.Count - 1;
                string stopAfter = !isCur ? chain[idx + 1].Anchor?.MsgId : null;
                if (stopAfter != null && !node.Messages.Any(m => m.Id == stopAfter)) stopAfter = null;
                bool firstUser = true;
                foreach (var m in node.Messages)
                {
                    if (isCur && uptoMsgId != null && m.Id == uptoMsgId) break;
                    if (!m.Pending && !string.IsNullOrEmpty(m.Content))
                    {
                        var c = m.Content;
                        List<string> images = null;
                        if (m.Role == "user")
                        {
                            if (firstUser && !string.IsNullOrEmpty(node.Anchor?.Text))
                                c = "关于你上面回答中的这段内容：\n「" + node.Anchor.Text + "」\n\n" + c;
                            firstUser = false;
                            if (m.Attachments != null)
                            {
                                foreach (var a in m.Attachments)
                                    if (a.Kind != "image" && !string.IsNullOrEmpty(a.Text))
                                        c += $"\n\n【附件：{a.Name}】\n{a.Text}";
                                var imgs = m.Images.ToList();
                                // 只给当前窗口的图片发原图，上层窗口的用文字代替，免得请求太大
                                if (imgs.Count > 0 && isCur && loadImage != null)
                                {
                                    images = new List<string>();
                                    foreach (var a in imgs) { var b64 = loadImage(a); if (b64 != null) images.Add(b64); }
                                    if (images.Count < imgs.Count) c += $"\n（有 {imgs.Count - images.Count} 张图片找不到了）";
                                    if (images.Count == 0) images = null;
                                }
                                else if (imgs.Count > 0) c += $"\n（这条提问附带了 {imgs.Count} 张图片）";
                            }
                        }
                        else if (dist > FullDepth && c.Length > TruncateChars)
                        {
                            c = c.Substring(0, TruncateChars) + "\n…（较早的回答，已截断）";
                        }
                        var role = m.Role == "assistant" ? "assistant" : "user";
                        if (msgs.Count > 0 && msgs[^1].Role == role)
                        {
                            msgs[^1].Content += "\n\n" + c;
                            if (images != null) (msgs[^1].Images ??= new List<string>()).AddRange(images);
                        }
                        else msgs.Add(new ChatMsg(role, c) { Images = images });
                    }
                    if (stopAfter != null && m.Id == stopAfter) break;
                }
            }
            if (msgs.Count > 0 && msgs[0].Role == "assistant") msgs.Insert(0, new ChatMsg("user", "（继续之前的讨论）"));
            // 太长时去掉最早的几轮（保留开头的主问题）
            // 角色是一问一答交替的：每次去掉第 2、3 条（一答一问），开头和顺序都不乱
            while (msgs.Count > 40) { msgs.RemoveAt(1); msgs.RemoveAt(1); }
            return msgs;
        }
    }

    /// <summary>对话保存在一个文件夹里，一个对话一个 json</summary>
    public class MindStore
    {
        public static readonly MindStore Shared = new MindStore();

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };

        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatSearcher", "对话数据");

        public string Folder
        {
            get
            {
                var f = (App.Settings?.MindFolder ?? "").Trim();
                return f.Length > 0 ? Environment.ExpandEnvironmentVariables(f) : DefaultFolder;
            }
        }

        readonly Dictionary<string, (MindSession s, DateTime mtime)> _cache = new Dictionary<string, (MindSession, DateTime)>();
        readonly Dictionary<string, DispatcherTimer> _timers = new Dictionary<string, DispatcherTimer>();
        string _cacheFolder;

        string PathOf(string id) => System.IO.Path.Combine(Folder, id + ".json");

        // ---------------- 附件 ----------------
        // 插件的布局：<库>/<Mindflow>/对话数据/*.json 和 <库>/<Mindflow>/附件/，消息里记「相对库根目录」的路径。
        // 这里一样：附件放在对话数据旁边的「附件」文件夹；往上找到 .obsidian 就以那里为根，找不到就以上一层为根。

        string ChatDir
        {
            get
            {
                var f = Folder.TrimEnd('\\', '/');
                return string.Equals(System.IO.Path.GetFileName(f), "对话数据", StringComparison.OrdinalIgnoreCase)
                    ? (System.IO.Path.GetDirectoryName(f) ?? f) : f;
            }
        }

        string BaseRoot
        {
            get
            {
                var chat = ChatDir;
                try
                {
                    for (var d = new DirectoryInfo(chat); d != null; d = d.Parent)
                        if (Directory.Exists(System.IO.Path.Combine(d.FullName, ".obsidian"))) return d.FullName;
                }
                catch { }
                return System.IO.Path.GetDirectoryName(chat) ?? chat;
            }
        }

        public string AttachDir => System.IO.Path.Combine(ChatDir, "附件");

        /// <summary>附件的完整路径</summary>
        public string Resolve(MindAttachment a)
        {
            if (string.IsNullOrEmpty(a?.Path)) return null;
            var rel = a.Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (System.IO.Path.IsPathRooted(rel)) return rel;
            var p = System.IO.Path.Combine(BaseRoot, rel);
            if (File.Exists(p)) return p;
            // 兜底：按文件名在附件文件夹里找
            var q = System.IO.Path.Combine(AttachDir, System.IO.Path.GetFileName(rel));
            return File.Exists(q) ? q : p;
        }

        /// <summary>把一张 JPEG 存进附件文件夹，返回附件记录</summary>
        public MindAttachment SaveImage(byte[] jpeg, string name)
        {
            Directory.CreateDirectory(AttachDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd");
            var baseName = string.IsNullOrWhiteSpace(name) ? "粘贴的图片" : name;
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, ' ');
            baseName = baseName.Replace('#', ' ').Replace('^', ' ').Replace('[', ' ').Replace(']', ' ').Trim();
            if (baseName.Length > 40) baseName = baseName.Substring(baseName.Length - 40);
            var file = System.IO.Path.Combine(AttachDir, $"{stamp}-{baseName}.jpg");
            for (int k = 2; File.Exists(file); k++) file = System.IO.Path.Combine(AttachDir, $"{stamp}-{baseName} {k}.jpg");
            File.WriteAllBytes(file, jpeg);
            var rel = System.IO.Path.GetRelativePath(BaseRoot, file).Replace('\\', '/');
            return new MindAttachment
            {
                Id = Mind.Uid(), Kind = "image", Name = System.IO.Path.GetFileName(file),
                Path = rel, Mime = "image/jpeg", Size = jpeg.Length,
            };
        }

        void CheckFolder()
        {
            if (_cacheFolder == Folder) return;
            Flush();
            _cache.Clear();
            _cacheFolder = Folder;
        }

        /// <summary>所有对话，最近的在前。文件没变就用缓存。</summary>
        public List<MindSession> List()
        {
            CheckFolder();
            var dir = Folder;
            var seen = new HashSet<string>();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                    {
                        var id = System.IO.Path.GetFileNameWithoutExtension(f);
                        seen.Add(id);
                        DateTime mt;
                        try { mt = File.GetLastWriteTimeUtc(f); } catch { continue; }
                        if (_cache.TryGetValue(id, out var c) && (c.mtime == mt || _timers.ContainsKey(id))) continue;
                        var s = Read(f);
                        if (s != null) _cache[id] = (s, mt);
                    }
                }
            }
            catch { }
            // 还没写到磁盘的新对话也算
            foreach (var id in _cache.Keys.ToList())
                if (!seen.Contains(id) && !_timers.ContainsKey(id) && !_unsaved.Contains(id)) _cache.Remove(id);
            return _cache.Values.Select(v => v.s).OrderByDescending(s => s.UpdatedAt).ToList();
        }

        readonly HashSet<string> _unsaved = new HashSet<string>();

        static MindSession Read(string file)
        {
            try
            {
                var s = JsonSerializer.Deserialize<MindSession>(File.ReadAllText(file, Encoding.UTF8), Json);
                if (s == null || string.IsNullOrEmpty(s.Id) || s.Nodes == null || s.Nodes.Count == 0) return null;
                foreach (var kv in s.Nodes)
                {
                    kv.Value.Id ??= kv.Key;
                    kv.Value.Messages ??= new List<MindMsg>();
                }
                if (s.RootId == null || !s.Nodes.ContainsKey(s.RootId))
                    s.RootId = s.Nodes.Values.FirstOrDefault(n => n.ParentId == null)?.Id ?? s.Nodes.Keys.First();
                return s;
            }
            catch { return null; }
        }

        public void Add(MindSession s)
        {
            CheckFolder();
            _cache[s.Id] = (s, DateTime.MinValue);
            _unsaved.Add(s.Id);
        }

        /// <summary>保存：默认合并 0.4 秒内的多次修改；immediate 立刻写</summary>
        public void Save(MindSession s, bool immediate = false)
        {
            s.UpdatedAt = Mind.Now();
            if (_timers.TryGetValue(s.Id, out var t)) t.Stop();
            if (immediate) { _timers.Remove(s.Id); Write(s); return; }
            if (t == null)
            {
                t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                var sess = s;
                t.Tick += (o, e) => { t.Stop(); _timers.Remove(sess.Id); Write(sess); };
                _timers[s.Id] = t;
            }
            t.Start();
        }

        public void Flush()
        {
            foreach (var kv in _timers.ToList())
            {
                kv.Value.Stop();
                if (_cache.TryGetValue(kv.Key, out var c)) Write(c.s);
            }
            _timers.Clear();
        }

        void Write(MindSession s)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var path = PathOf(s.Id);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json), new UTF8Encoding(false));
                File.Move(tmp, path, true);
                _unsaved.Remove(s.Id);
                _cache[s.Id] = (s, File.GetLastWriteTimeUtc(path));
            }
            catch { }
        }

        public void Delete(string id)
        {
            if (_timers.TryGetValue(id, out var t)) { t.Stop(); _timers.Remove(id); }
            _cache.Remove(id);
            _unsaved.Remove(id);
            try { var p = PathOf(id); if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
