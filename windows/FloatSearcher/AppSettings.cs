using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace FloatSearcher
{
    /// <summary>三种模式：点悬浮图标时打开哪一种</summary>
    public enum Mode { Translate, Chat, Clipboard }

    /// <summary>剪贴板模式：粘贴后自动发送，还是等用户再输入提示词</summary>
    public enum ClipAction { AutoSend, AskFirst }

    /// <summary>所有设置，保存在 %APPDATA%\FloatSearcher\settings.json</summary>
    public class AppSettings
    {
        // ---- AI 接口 ----
        public string Provider { get; set; } = Providers.DeepSeek;
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        public bool DeepThink { get; set; } = false;
        /// <summary>DeepSeek 联网搜索（AI 对话、剪贴板模式）</summary>
        public bool WebSearch { get; set; } = true;

        // ---- 当前模式（扇形菜单里选的，下次还是它） ----
        public Mode Mode { get; set; } = Mode.Translate;

        // ---- 中译英 ----
        public bool TrSynonyms { get; set; } = true;
        public int TrSynonymCount { get; set; } = 3;
        public bool TrVariants { get; set; } = true;
        /// <summary>句子的不同表达方式，每行一种</summary>
        public string TrVariantList { get; set; } = "日常口语\n正式书面\n简洁版";
        public bool TrGrammar { get; set; } = false;
        public bool TrExample { get; set; } = false;
        public string TrExtra { get; set; } = "";
        public bool TrAutoHide { get; set; } = true;

        // ---- AI 对话 ----
        public string ChatSystem { get; set; } =
            "你是桌面悬浮助手。回答简洁清晰，先给结论再解释；需要时用短列表，少用标题和表格。默认用简体中文回答。";

        // ---- 剪贴板 ----
        public ClipAction ClipAction { get; set; } = ClipAction.AutoSend;
        public string ClipPrompt { get; set; } = "请解释这段内容。如果是外文，先翻译成中文再解释。";
        /// <summary>剪贴板模式下的快捷提示词，每行「名称 | 指令」</summary>
        public string ClipPresets { get; set; } =
            "解释 | 用通俗的话解释这段内容。\n" +
            "翻译成中文 | 把这段内容翻译成自然流畅的中文，只给译文。\n" +
            "翻译成英文 | 把这段内容翻译成地道的英文，只给译文。\n" +
            "总结 | 用三句话以内总结要点。\n" +
            "润色 | 在不改变原意的前提下润色这段文字，直接给出改好的版本。\n" +
            "怎么回复 | 这是别人发给我的消息，帮我写两三个得体的回复。";

        // ---- 悬浮图标 ----
        public double BubbleX { get; set; } = double.NaN;
        public double BubbleY { get; set; } = double.NaN;
        public double BubbleOpacity { get; set; } = 0.95;

        // ================= 读写 =================

        static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatSearcher");
        static readonly string FilePath = Path.Combine(Dir, "settings.json");

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch { }
        }

        // ================= 派生值 =================

        public string EffectiveBaseUrl =>
            string.IsNullOrWhiteSpace(BaseUrl) ? Providers.DefaultBase(Provider) : BaseUrl.Trim();

        public string EffectiveModel =>
            string.IsNullOrWhiteSpace(Model) ? Providers.DefaultModel(Provider) : Model.Trim();

        public List<string> VariantNames()
        {
            var list = new List<string>();
            foreach (var raw in (TrVariantList ?? "").Split('\n'))
            {
                var t = raw.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public List<KeyValuePair<string, string>> ClipPresetList()
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var raw in (ClipPresets ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                int i = line.IndexOfAny(new[] { '|', '｜' });
                if (i > 0)
                {
                    var name = line.Substring(0, i).Trim();
                    var prompt = line.Substring(i + 1).Trim();
                    if (name.Length > 0 && prompt.Length > 0) list.Add(new KeyValuePair<string, string>(name, prompt));
                }
                else list.Add(new KeyValuePair<string, string>(line.Length > 6 ? line.Substring(0, 6) + "…" : line, line));
            }
            return list;
        }

        // ================= 开机自启 =================

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "FloatSearcher";

        public static bool AutoStart
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
                    return k?.GetValue(RunName) != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                    if (value) k.SetValue(RunName, "\"" + Environment.ProcessPath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                catch { }
            }
        }
    }

    public static class Providers
    {
        public const string Anthropic = "anthropic";
        public const string DeepSeek = "deepseek";
        public const string OpenAI = "openai";

        public static string DefaultBase(string p) => p switch
        {
            Anthropic => "https://api.anthropic.com",
            DeepSeek => "https://api.deepseek.com",
            _ => "https://api.openai.com/v1",
        };

        public static string DefaultModel(string p) => p switch
        {
            Anthropic => "claude-sonnet-5-5",
            DeepSeek => "deepseek-flash",
            _ => "",
        };
    }
}
