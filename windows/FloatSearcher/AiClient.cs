using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FloatSearcher
{
    public class ChatMsg
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public ChatMsg(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>出错时给用户看的中文说明</summary>
    public class AiException : Exception
    {
        public AiException(string message) : base(message) { }
    }

    /// <summary>
    /// 流式调用 AI：Claude（Anthropic）、DeepSeek、或任何 OpenAI 兼容接口。
    /// 在界面线程上 await 时，onDelta 也在界面线程回调。
    /// </summary>
    public class AiClient
    {
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        CancellationTokenSource _cts;

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        /// <summary>
        /// 发送并流式接收。返回完整回答；被 Cancel 时抛 OperationCanceledException；出错抛 AiException。
        /// </summary>
        public async Task<string> StreamAsync(
            AppSettings s, string system, IList<ChatMsg> history, Action<string> onDelta,
            bool noThinking = false, Action<int> onThinking = null)
        {
            Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            var key = (s.ApiKey ?? "").Trim();
            var model = s.EffectiveModel;
            var baseUrl = s.EffectiveBaseUrl.TrimEnd('/');
            if (key.Length == 0) throw new AiException("还没填 API Key：右键悬浮图标 → 设置。");
            if (model.Length == 0) throw new AiException("还没填模型名：右键悬浮图标 → 设置。");

            bool anthropic = s.Provider == Providers.Anthropic;
            HttpRequestMessage req = anthropic
                ? BuildAnthropic(baseUrl, key, model, system, history)
                : BuildOpenAi(baseUrl, key, model, system, history, s.Provider == Providers.DeepSeek, s.DeepThink && !noThinking);

            HttpResponseMessage resp;
            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                headerCts.CancelAfter(TimeSpan.FromSeconds(40));
                try
                {
                    resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new AiException("等太久没有回应，请检查网络后再试。");
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                catch (HttpRequestException e)
                {
                    throw new AiException("连不上服务器：请检查网络，或接口地址是否写对。（" + e.Message + "）");
                }
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string body = "";
                    try { body = await resp.Content.ReadAsStringAsync(token); } catch { }
                    token.ThrowIfCancellationRequested();
                    throw new AiException(DescribeHttp((int)resp.StatusCode, body));
                }

                var sb = new StringBuilder();
                int thought = 0;
                using var stream = await resp.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string line;
                    try { line = await reader.ReadLineAsync(token); }
                    catch (Exception) when (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }
                    catch (IOException)
                    {
                        throw new AiException("连接中断了，请再试一次。");
                    }
                    token.ThrowIfCancellationRequested(); // 停止后不再回调
                    if (line == null) break;
                    if (!line.StartsWith("data:")) continue;
                    var data = line.Substring(5).Trim();
                    if (data.Length == 0) continue;
                    if (data == "[DONE]") break;

                    string piece, reasoning = null;
                    try
                    {
                        if (anthropic) piece = ParseAnthropic(data);
                        else piece = ParseOpenAi(data, out reasoning);
                    }
                    catch (JsonException) { continue; } // 偶尔有不完整的行，跳过

                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        thought += reasoning.Length;
                        onThinking?.Invoke(thought);
                    }
                    if (!string.IsNullOrEmpty(piece))
                    {
                        sb.Append(piece);
                        onDelta?.Invoke(piece);
                    }
                }
                token.ThrowIfCancellationRequested();
                if (ReferenceEquals(_cts, cts)) _cts = null;
                return sb.ToString();
            }
        }

        // ---------------- Claude ----------------

        static HttpRequestMessage BuildAnthropic(string baseUrl, string key, string model, string system, IList<ChatMsg> history)
        {
            var url = baseUrl.EndsWith("/v1") ? baseUrl + "/messages" : baseUrl + "/v1/messages";
            var msgs = new JsonArray();
            foreach (var m in history)
                msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 2048,
                ["stream"] = true,
                ["system"] = system,
                ["messages"] = msgs,
            };
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return req;
        }

        static string ParseAnthropic(string data)
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "content_block_delta" &&
                root.TryGetProperty("delta", out var d) &&
                d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta" &&
                d.TryGetProperty("text", out var text))
                return text.GetString();
            if (type == "error")
            {
                var msg = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                    ? em.GetString() : "服务返回错误";
                throw new AiException(msg);
            }
            return null;
        }

        // ---------------- OpenAI 兼容 / DeepSeek ----------------

        static HttpRequestMessage BuildOpenAi(string baseUrl, string key, string model, string system,
            IList<ChatMsg> history, bool deepseek, bool think)
        {
            var url = baseUrl.EndsWith("/chat/completions") ? baseUrl : baseUrl + "/chat/completions";
            var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
            foreach (var m in history)
                msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["messages"] = msgs,
            };
            // DeepSeek 默认开启思考；这里默认关掉求快，设置里可打开
            if (deepseek) body["thinking"] = new JsonObject { ["type"] = think ? "enabled" : "disabled" };
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return req;
        }

        static string ParseOpenAi(string data, out string reasoning)
        {
            reasoning = null;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var msg = err.TryGetProperty("message", out var em) ? em.GetString() : "服务返回错误";
                throw new AiException(msg);
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0) return null;
            var c0 = choices[0];
            if (!c0.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return null;
            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                reasoning = rc.GetString();
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
            return null;
        }

        // ---------------- 错误说明 ----------------

        static string DescribeHttp(int code, string body)
        {
            string detail = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) detail = m.GetString();
                    else if (e.ValueKind == JsonValueKind.String) detail = e.GetString();
                }
                if (detail == null && root.TryGetProperty("message", out var m2)) detail = m2.GetString();
            }
            catch { }
            if (string.IsNullOrWhiteSpace(detail)) detail = body.Length > 160 ? body.Substring(0, 160) : body;
            string hint = code switch
            {
                400 => "请求有误，常见原因是模型名写错",
                401 => "API Key 不对或已失效",
                402 => "账户余额不足",
                403 => "这个 Key 没有权限，或所在地区不支持",
                404 => "接口地址或模型名不对",
                429 => "请求太频繁，或额度用完了",
                500 or 502 or 503 or 529 => "服务暂时繁忙，稍后再试",
                _ => "请求失败",
            };
            return string.IsNullOrWhiteSpace(detail) ? $"{hint}（{code}）" : $"{hint}（{code}）：{detail.Trim()}";
        }
    }
}
