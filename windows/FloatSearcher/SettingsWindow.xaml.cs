using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace FloatSearcher
{
    public partial class SettingsWindow : Window
    {
        readonly AiClient _test = new AiClient();

        public SettingsWindow()
        {
            InitializeComponent();
            Load();

            PDeepSeek.Checked += (s, e) => OnProviderChanged();
            PClaude.Checked += (s, e) => OnProviderChanged();
            POpenAI.Checked += (s, e) => OnProviderChanged();
            PickFlash.Click += (s, e) => ModelBox.Text = "deepseek-flash";
            PickPro.Click += (s, e) => ModelBox.Text = "deepseek-v4-pro";
            TestBtn.Click += async (s, e) => await TestConnection();
            OpacitySlider.ValueChanged += (s, e) => OpacityText.Text = $"{OpacitySlider.Value * 100:0}%";
            SaveBtn.Click += (s, e) => { SaveAll(); SavedHint.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss"); };
            CancelBtn.Click += (s, e) => Close();
            Closed += (s, e) => _test.Cancel();
        }

        string _lastProvider;

        void Load()
        {
            var s = App.Settings;
            _lastProvider = s.Provider;
            PDeepSeek.IsChecked = s.Provider == Providers.DeepSeek;
            PClaude.IsChecked = s.Provider == Providers.Anthropic;
            POpenAI.IsChecked = s.Provider == Providers.OpenAI;
            BaseUrl.Text = s.BaseUrl;
            ApiKey.Password = s.ApiKey;
            ModelBox.Text = string.IsNullOrWhiteSpace(s.Model) ? Providers.DefaultModel(s.Provider) : s.Model;
            DeepThink.IsChecked = s.DeepThink;
            UpdateProviderHint();

            for (int i = 1; i <= 6; i++) TrSynCount.Items.Add(i.ToString());
            TrSynCount.SelectedIndex = Math.Max(0, Math.Min(5, s.TrSynonymCount - 1));
            TrSyn.IsChecked = s.TrSynonyms;
            TrVar.IsChecked = s.TrVariants;
            TrVarList.Text = s.TrVariantList;
            TrExample.IsChecked = s.TrExample;
            TrGrammar.IsChecked = s.TrGrammar;
            TrExtra.Text = s.TrExtra;
            TrAutoHide.IsChecked = s.TrAutoHide;

            ChatSystem.Text = s.ChatSystem;

            ClipAuto.IsChecked = s.ClipAction == ClipAction.AutoSend;
            ClipAsk.IsChecked = s.ClipAction == ClipAction.AskFirst;
            ClipPrompt.Text = s.ClipPrompt;
            ClipPresets.Text = s.ClipPresets;

            AutoStart.IsChecked = AppSettings.AutoStart;
            OpacitySlider.Value = s.BubbleOpacity;
            OpacityText.Text = $"{s.BubbleOpacity * 100:0}%";
        }

        string CurrentProvider() =>
            PClaude.IsChecked == true ? Providers.Anthropic :
            POpenAI.IsChecked == true ? Providers.OpenAI : Providers.DeepSeek;

        void OnProviderChanged()
        {
            var now = CurrentProvider();
            if (now == _lastProvider) return;
            // 地址/模型还是上一家的默认值时，换成这一家的
            if (string.IsNullOrWhiteSpace(BaseUrl.Text) || BaseUrl.Text.Trim() == Providers.DefaultBase(_lastProvider))
                BaseUrl.Text = "";
            var known = new List<string> { Providers.DefaultModel(Providers.Anthropic), Providers.DefaultModel(Providers.DeepSeek), "deepseek-v4-pro" };
            if (string.IsNullOrWhiteSpace(ModelBox.Text) || known.Contains(ModelBox.Text.Trim()))
                ModelBox.Text = Providers.DefaultModel(now);
            _lastProvider = now;
            UpdateProviderHint();
        }

        void UpdateProviderHint()
        {
            var p = CurrentProvider();
            DeepSeekBox.Visibility = p == Providers.DeepSeek ? Visibility.Visible : Visibility.Collapsed;
            ProvHint.Text = p switch
            {
                Providers.DeepSeek => "DeepSeek 官方接口，默认地址 https://api.deepseek.com 。Key 在 platform.deepseek.com 的「API keys」里创建。",
                Providers.Anthropic => "Anthropic 官方 Claude 接口，默认地址 https://api.anthropic.com 。Key 在 console.anthropic.com 获取。",
                _ => "OpenAI 或兼容它的服务（通义千问、Kimi、智谱等）。地址填到 /v1 这一级，模型名照服务商给的填。",
            };
        }

        void SaveAll()
        {
            var s = App.Settings;
            s.Provider = CurrentProvider();
            s.BaseUrl = BaseUrl.Text.Trim();
            s.ApiKey = ApiKey.Password.Trim();
            s.Model = ModelBox.Text.Trim();
            s.DeepThink = DeepThink.IsChecked == true;

            s.TrSynonyms = TrSyn.IsChecked == true;
            s.TrSynonymCount = TrSynCount.SelectedIndex + 1;
            s.TrVariants = TrVar.IsChecked == true;
            s.TrVariantList = TrVarList.Text.Replace("\r", "");
            s.TrExample = TrExample.IsChecked == true;
            s.TrGrammar = TrGrammar.IsChecked == true;
            s.TrExtra = TrExtra.Text.Replace("\r", "").Trim();
            s.TrAutoHide = TrAutoHide.IsChecked == true;

            if (!string.IsNullOrWhiteSpace(ChatSystem.Text)) s.ChatSystem = ChatSystem.Text.Replace("\r", "").Trim();

            s.ClipAction = ClipAsk.IsChecked == true ? ClipAction.AskFirst : ClipAction.AutoSend;
            if (!string.IsNullOrWhiteSpace(ClipPrompt.Text)) s.ClipPrompt = ClipPrompt.Text.Replace("\r", "").Trim();
            s.ClipPresets = ClipPresets.Text.Replace("\r", "");

            AppSettings.AutoStart = AutoStart.IsChecked == true;
            s.BubbleOpacity = Math.Round(OpacitySlider.Value, 2);

            s.Save();
            InputBar.ClearTranslateCache();
            App.Bubble?.RefreshMode();
        }

        async System.Threading.Tasks.Task TestConnection()
        {
            SaveAll();
            TestBtn.IsEnabled = false;
            TestResult.Foreground = App.Brush("InkSoft");
            TestResult.Text = "连接中…";
            try
            {
                var r = await _test.StreamAsync(App.Settings, "只回复四个字：连接成功",
                    new[] { new ChatMsg("user", "测试") }, piece => { }, noThinking: true);
                TestResult.Foreground = App.Brush("Accent");
                TestResult.Text = "✓ 可以用了：" + (r.Length > 30 ? r.Substring(0, 30) : r).Trim();
            }
            catch (OperationCanceledException) { TestResult.Text = ""; }
            catch (Exception e)
            {
                TestResult.Foreground = App.Brush("Danger");
                TestResult.Text = e.Message;
            }
            finally { TestBtn.IsEnabled = true; }
        }
    }
}
