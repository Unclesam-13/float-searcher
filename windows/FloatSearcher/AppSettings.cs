using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace FloatSearcher
{
    /// <summary>两种模式：点悬浮图标时打开哪一种</summary>
    public enum Mode { Translate, Chat }

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

        /// <summary>划线追问的预设问题，每行一个</summary>
        public string MindPresets { get; set; } =
            "请详细解释这段内容。\n能举一个具体的例子吗？\n用通俗易懂的话再讲一遍。\n这和前面的内容有什么联系？";
        /// <summary>划线后直接回车追问时用的问题</summary>
        public string MindDefaultFollowUp { get; set; } = "请详细解释这段内容。";
        /// <summary>对话历史保存位置；空 = %APPDATA%\FloatSearcher\对话数据</summary>
        public string MindFolder { get; set; } = "";

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
                    if (s != null)
                    {
                        // 旧版本的「剪贴板」模式已经去掉了：改成 AI 对话
                        if (s.Mode != Mode.Translate && s.Mode != Mode.Chat) s.Mode = Mode.Chat;
                        return s;
                    }
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

        public List<string> MindPresetList()
        {
            var list = new List<string>();
            foreach (var raw in (MindPresets ?? "").Split('\n'))
            {
                var t = raw.Trim();
                if (t.Length > 0) list.Add(t);
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
