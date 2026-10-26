using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatSearcher
{
    /// <summary>
    /// 把 AI 的 Markdown 回答排成 FlowDocument（标题、粗体、代码、列表、引用），
    /// 并把已经划线追问过的句子标成黄色高亮，点高亮打开那条追问。
    /// </summary>
    public static class MindRender
    {
        const byte Bold = 1, Code = 2, Heading = 4, Quote = 8, CodeBlock = 16;

        static readonly Regex InlineRx = new Regex(@"(\*\*[^*\n]+?\*\*|__[^_\n]+?__|`[^`\n]+`)", RegexOptions.Compiled);
        static readonly Regex TableSep = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
        static readonly Regex Ordered = new Regex(@"^(\d{1,3})[.)]\s+", RegexOptions.Compiled);

        /// <summary>Markdown → 显示用的纯文字（每个字带格式标记）</summary>
        static void Flatten(string md, StringBuilder text, List<byte> flags)
        {
            bool inCode = false;
            bool first = true;
            foreach (var raw in (md ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw;
                var t = line.TrimStart();
                if (t.StartsWith("```"))
                {
                    inCode = !inCode;
                    continue;
                }
                if (!first) { text.Append('\n'); flags.Add(0); }
                first = false;

                if (inCode)
                {
                    foreach (var ch in line) { text.Append(ch); flags.Add(CodeBlock); }
                    continue;
                }

                byte lineFlag = 0;
                string indent = line.Substring(0, line.Length - t.Length);
                if (indent.Length > 8) indent = indent.Substring(0, 8);
                indent = indent.Replace("\t", "    ");
                string prefix = "";

                if (t.StartsWith("#"))
                {
                    t = t.TrimStart('#').TrimStart();
                    lineFlag = Heading;
                    indent = "";
                }
                else if (t == "---" || t == "***" || t == "___")
                {
                    continue; // 分隔线留空行
                }
                else if (t.StartsWith(">"))
                {
                    t = t.TrimStart('>').TrimStart();
                    lineFlag = Quote;
                    prefix = "│ ";
                }
                else if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ "))
                {
                    t = t.Substring(2);
                    if (t.StartsWith("[ ] ")) { prefix = "☐ "; t = t.Substring(4); }
                    else if (t.StartsWith("[x] ") || t.StartsWith("[X] ")) { prefix = "☑ "; t = t.Substring(4); }
                    else prefix = "• ";
                }
                else if (TableSep.IsMatch(t))
                {
                    continue;
                }
                else if (t.StartsWith("|") && t.TrimEnd().EndsWith("|") && t.Length > 1)
                {
                    var cells = t.Trim().Trim('|').Split('|');
                    for (int i = 0; i < cells.Length; i++) cells[i] = cells[i].Trim();
                    t = string.Join("   ·   ", cells);
                }

                foreach (var ch in indent + prefix) { text.Append(ch); flags.Add(lineFlag); }
                foreach (var part in InlineRx.Split(t))
                {
                    if (part.Length == 0) continue;
                    byte f = lineFlag;
                    string s = part;
                    if ((s.StartsWith("**") || s.StartsWith("__")) && s.Length >= 4 && InlineRx.IsMatch(s) && InlineRx.Match(s).Length == s.Length)
                    {
                        s = s.Substring(2, s.Length - 4);
                        f |= Bold;
                    }
                    else if (s.StartsWith("`") && s.Length >= 2 && s.EndsWith("`") && InlineRx.Match(s).Length == s.Length)
                    {
                        s = s.Substring(1, s.Length - 2);
                        f |= Code;
                    }
                    foreach (var ch in s) { text.Append(ch); flags.Add(f); }
                }
            }
        }

        /// <summary>纯文字版（复制、算字数用）</summary>
        public static string Plain(string md)
        {
            var sb = new StringBuilder();
            Flatten(md, sb, new List<byte>());
            return sb.ToString();
        }

        /// <summary>比较时忽略空白和 Markdown 符号，划线原文从这边或 Obsidian 来都能对上</summary>
        static bool Skip(char c) => char.IsWhiteSpace(c) || c == '•' || c == '*' || c == '`' || c == '#' || c == '>' || c == '│' || c == '$' || c == '☐' || c == '☑';

        public static string Condense(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "") if (!Skip(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>选中的文字整理成划线原文：去掉列表圆点等</summary>
        public static string CleanSelection(string s)
        {
            var lines = (s ?? "").Replace("\r", "").Split('\n');
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.StartsWith("• ") || t.StartsWith("│ ") || t.StartsWith("☐ ") || t.StartsWith("☑ ")) t = t.Substring(2);
                if (t.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            return sb.ToString().Trim();
        }

        public class Anchor
        {
            public string Text;
            public string ChildId;
            public string Tip;
        }

        public static FlowDocument Build(string md, IList<Anchor> anchors, Action<string> onAnchor, double fontSize)
        {
            var text = new StringBuilder();
            var flags = new List<byte>();
            Flatten(md, text, flags);
            var plain = text.ToString();

            // 高亮：每个字属于哪条追问（-1 = 没有）
            var hl = new int[plain.Length];
            for (int i = 0; i < hl.Length; i++) hl[i] = -1;
            if (anchors != null && anchors.Count > 0)
            {
                var map = new List<int>();
                var cond = new StringBuilder();
                for (int i = 0; i < plain.Length; i++)
                    if (!Skip(plain[i])) { cond.Append(plain[i]); map.Add(i); }
                var cs = cond.ToString();
                for (int a = 0; a < anchors.Count; a++)
                {
                    var key = Condense(anchors[a].Text);
                    if (key.Length == 0) continue;
                    int at = cs.IndexOf(key, StringComparison.Ordinal);
                    if (at < 0) continue;
                    int start = map[at], end = map[at + key.Length - 1] + 1;
                    for (int k = start; k < end; k++) if (hl[k] < 0) hl[k] = a;
                }
            }

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = fontSize,
                FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
                Foreground = App.Brush("Ink"),
                TextAlignment = TextAlignment.Left,
            };
            var hlBrush = App.Brush("Mark");

            int pos = 0;
            bool gap = false;
            foreach (var line in plain.Split('\n'))
            {
                int ls = pos;
                pos += line.Length + 1;
                if (line.Trim().Length == 0) { gap = true; continue; }

                byte lf = flags.Count > ls ? flags[ls] : (byte)0;
                var p = new Paragraph { Margin = new Thickness(0, gap ? 8 : 2, 0, 0), LineHeight = fontSize * 1.55 };
                if (doc.Blocks.Count == 0) p.Margin = new Thickness(0);
                gap = false;
                if ((lf & Heading) != 0) { p.FontWeight = FontWeights.Bold; p.FontSize = fontSize + 1.5; if (doc.Blocks.Count > 0) p.Margin = new Thickness(0, 10, 0, 0); }
                if ((lf & Quote) != 0) p.Foreground = App.Brush("InkSoft");
                if ((lf & CodeBlock) != 0)
                {
                    p.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                    p.FontSize = fontSize - 1.5;
                    p.Background = App.Brush("Surface2");
                    p.Margin = new Thickness(0, 0, 0, 0);
                    p.Padding = new Thickness(8, 1, 8, 1);
                    p.LineHeight = fontSize * 1.4;
                }

                int i = 0;
                while (i < line.Length)
                {
                    int gi = ls + i;
                    byte f = flags.Count > gi ? flags[gi] : (byte)0;
                    int h = hl[gi];
                    int j = i + 1;
                    while (j < line.Length && (flags.Count > ls + j ? flags[ls + j] : 0) == f && hl[ls + j] == h) j++;
                    var run = new Run(line.Substring(i, j - i));
                    if ((f & Bold) != 0) run.FontWeight = FontWeights.SemiBold;
                    if ((f & Code) != 0)
                    {
                        run.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
                        run.Background = App.Brush("Surface2");
                    }
                    if (h >= 0 && onAnchor != null)
                    {
                        var a = anchors[h];
                        var link = new Hyperlink(run)
                        {
                            Background = hlBrush,
                            Foreground = App.Brush("Ink"),
                            TextDecorations = null,
                            Cursor = Cursors.Hand,
                            ToolTip = a.Tip ?? "已追问 · 点击打开这条追问",
                            Focusable = false,
                        };
                        var id = a.ChildId;
                        link.Click += (s, e) => onAnchor(id);
                        p.Inlines.Add(link);
                    }
                    else p.Inlines.Add(run);
                    i = j;
                }
                doc.Blocks.Add(p);
            }
            if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph(new Run("")) { Margin = new Thickness(0) });
            return doc;
        }
    }
}
