using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    /// <summary>一条搜索结果（编号在整次回答里唯一，回答里用 [编号] 引用）</summary>
    public class SearchHit
    {
        public int Index;
        public string Title;
        public string Url;
        public string Site;
        public string Date;
        public string Summary;
    }

    /// <summary>联网时的过程回调</summary>
    public class WebCallbacks
    {
        /// <summary>开始搜索某个关键词</summary>
        public Action<string> OnSearch;
        /// <summary>之前流出来的文字作废（模型决定先去搜索）</summary>
        public Action OnReset;
        /// <summary>回答完成后，引用到的来源</summary>
        public Action<List<SearchHit>> OnSources;
    }

    /// <summary>
    /// 流式调用 AI：Claude（Anthropic）、DeepSeek、或任何 OpenAI 兼容接口。
    /// DeepSeek 可以联网：模型自己决定要不要搜、搜什么，程序调用 DeepSeek 的搜索接口把结果交给它。
    /// 在界面线程上 await 时，所有回调也在界面线程。
    /// </summary>
    public class AiClient
    {
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        const int MaxSearchRounds = 3;

        CancellationTokenSource _cts;

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        /// <summary>
        /// 发送并流式接收。返回完整回答；被 Cancel 时抛 OperationCanceledException；出错抛 AiException。
        /// web 不为空且接口是 DeepSeek 时，允许联网搜索。
        /// </summary>
        public async Task<string> StreamAsync(
            AppSettings s, string system, IList<ChatMsg> history, Action<string> onDelta,
            bool noThinking = false, Action<int> onThinking = null, WebCallbacks web = null)
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

            string result;
            if (s.Provider == Providers.Anthropic)
            {
                var req = BuildAnthropic(baseUrl, key, model, system, history);
                result = (await RunRound(req, true, token, onDelta, onThinking, null)).Content;
            }
            else if (web != null && s.Provider == Providers.DeepSeek)
            {
                result = await WebLoop(baseUrl, key, model, system, history, token, onDelta, web);
            }
            else
            {
                var body = OpenAiBody(model, system, history, s.Provider == Providers.DeepSeek, s.DeepThink && !noThinking);
                result = (await RunRound(OpenAiRequest(baseUrl, key, body), false, token, onDelta, onThinking, null)).Content;
            }
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_cts, cts)) _cts = null;
            return result;
        }

        // ================= 一轮请求：发出去，流式读回来 =================

        class ToolCall
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new StringBuilder();
        }

        class RoundResult
        {
            public string Content = "";
            public string Reasoning = "";
            public List<ToolCall> Calls = new List<ToolCall>();
        }

        async Task<RoundResult> RunRound(HttpRequestMessage req, bool anthropic, CancellationToken token,
            Action<string> onDelta, Action<int> onThinking, SortedDictionary<int, ToolCall> tools)
        {
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

                var result = new RoundResult();
                var sb = new StringBuilder();
                var think = new StringBuilder();
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
                        else piece = ParseOpenAi(data, out reasoning, tools);
                    }
                    catch (JsonException) { continue; } // 偶尔有不完整的行，跳过

                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        think.Append(reasoning);
                        onThinking?.Invoke(think.Length);
                    }
                    if (!string.IsNullOrEmpty(piece))
                    {
                        sb.Append(piece);
                        onDelta?.Invoke(piece);
                    }
                }
                result.Content = sb.ToString();
                result.Reasoning = think.ToString();
                if (tools != null) result.Calls.AddRange(tools.Values);
                return result;
            }
        }

        // ================= DeepSeek 联网 =================

        static readonly JsonObject SearchTool = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "web_search",
                ["description"] = "联网搜索网页，返回编号、标题、网址、发布日期和摘要。需要最新信息、新闻、价格、版本、具体数据或核实事实时使用。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "搜索关键词，简短明确，不超过 60 个字。可以用 site:、after:2025、lang:zh,en 等语法。",
                        },
                    },
                    ["required"] = new JsonArray("query"),
                },
            },
        };

        public static string WebSystemNote() =>
            "\n\n你可以使用 web_search 工具联网搜索。涉及最新动态、新闻、价格、版本、天气、具体数据，或你不确定的事实时，先搜索再回答；" +
            "常识和不需要时效的问题不必搜索。最多搜索 3 次。" +
            "回答时，在用到搜索结果的句子末尾标注来源编号，例如 [1] 或 [2][3]，编号和搜索结果里的一致；不要在末尾另列来源清单。" +
            "今天是 " + DateTime.Now.ToString("yyyy年M月d日") + "。";

        async Task<string> WebLoop(string baseUrl, string key, string model, string system, IList<ChatMsg> history,
            CancellationToken token, Action<string> onDelta, WebCallbacks web)
        {
            var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system + WebSystemNote() } };
            foreach (var m in history)
                msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            string question = "";
            for (int i = history.Count - 1; i >= 0; i--)
                if (history[i].Role == "user") { question = history[i].Content; break; }

            var hits = new List<SearchHit>();
            for (int round = 0; ; round++)
            {
                bool allowTools = round < MaxSearchRounds;
                var body = new JsonObject
                {
                    ["model"] = model,
                    ["stream"] = true,
                    ["messages"] = msgs.DeepClone(),
                    // 联网时关闭深度思考：带工具的思考模式要求每次都回传思考内容，容易出错
                    ["thinking"] = new JsonObject { ["type"] = "disabled" },
                };
                if (allowTools) body["tools"] = new JsonArray(SearchTool.DeepClone());

                var tools = allowTools ? new SortedDictionary<int, ToolCall>() : null;
                var r = await RunRound(OpenAiRequest(baseUrl, key, body), false, token, onDelta, null, tools);

                var calls = r.Calls.FindAll(c => c.Name == "web_search");
                if (calls.Count == 0)
                {
                    web.OnSources?.Invoke(CitedSources(r.Content, hits));
                    return r.Content;
                }

                // 模型要去搜索：前面流出来的「我来搜一下」之类不显示
                web.OnReset?.Invoke();

                var toolCalls = new JsonArray();
                for (int i = 0; i < calls.Count; i++)
                    if (string.IsNullOrEmpty(calls[i].Id)) calls[i].Id = $"call_{round}_{i}";
                foreach (var c in calls)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToString() },
                    });
                }
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = r.Content ?? "", ["tool_calls"] = toolCalls };
                if (!string.IsNullOrEmpty(r.Reasoning)) assistant["reasoning_content"] = r.Reasoning;
                msgs.Add(assistant);

                foreach (var c in calls)
                {
                    string q = QueryFrom(c.Args.ToString());
                    string toolText;
                    if (q.Length == 0) toolText = "搜索关键词为空。";
                    else
                    {
                        web.OnSearch?.Invoke(q);
                        try
                        {
                            var found = await Search(baseUrl, key, q, question, token);
                            foreach (var h in found) { h.Index = hits.Count + 1; hits.Add(h); }
                            toolText = FormatHits(q, found);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { toolText = "搜索失败：" + e.Message + "。请根据已有知识回答，并说明没能联网核实。"; }
                    }
                    msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = toolText });
                }
            }
        }

        static string QueryFrom(string args)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
                if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                    return q.GetString().Trim();
            }
            catch { }
            return "";
        }

        static string Clip(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        /// <summary>调用 DeepSeek 搜索接口：POST /v1/tools/search</summary>
        static async Task<List<SearchHit>> Search(string baseUrl, string key, string query, string question, CancellationToken token)
        {
            var root = baseUrl.EndsWith("/v1") ? baseUrl.Substring(0, baseUrl.Length - 3) : baseUrl;
            var body = new JsonObject
            {
                ["query"] = Clip(query, 128),
                ["count"] = 6,
            };
            var oq = Clip(question.Replace("\n", " "), 128);
            if (oq.Length > 0) body["ori_question"] = oq;

            var req = new HttpRequestMessage(HttpMethod.Post, root + "/v1/tools/search")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, cts.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new Exception("搜索超时"); }

            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(token);
                if (!resp.IsSuccessStatusCode) throw new Exception(DescribeHttp((int)resp.StatusCode, text));
                var list = new List<SearchHit>();
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in arr.EnumerateArray())
                    {
                        string Str(string name) =>
                            it.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                        var url = Str("url");
                        if (url.Length == 0) continue;
                        var summary = Str("summary");
                        if (summary.Length == 0) summary = Str("snippet");
                        list.Add(new SearchHit
                        {
                            Title = Str("title"),
                            Url = url,
                            Site = Str("site_name"),
                            Date = Str("date"),
                            Summary = Clip(summary, 800),
                        });
                    }
                }
                return list;
            }
        }

        static string FormatHits(string query, List<SearchHit> hits)
        {
            if (hits.Count == 0) return $"搜索「{query}」没有找到结果。";
            var sb = new StringBuilder();
            sb.Append("搜索「").Append(query).Append("」的结果：\n");
            foreach (var h in hits)
            {
                sb.Append('\n').Append('[').Append(h.Index).Append("] ").Append(h.Title);
                if (h.Site.Length > 0) sb.Append(" — ").Append(h.Site);
                if (h.Date.Length >= 10) sb.Append(" · ").Append(h.Date.Substring(0, 10));
                sb.Append('\n').Append(h.Url).Append('\n').Append(h.Summary).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>回答里引用了哪些 [编号]；一个都没引用时给前 5 条</summary>
        static List<SearchHit> CitedSources(string answer, List<SearchHit> hits)
        {
            var used = new SortedSet<int>();
            foreach (Match m in Regex.Matches(answer ?? "", @"\[(\d{1,2})\]"))
                if (int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= hits.Count) used.Add(n);
            var list = new List<SearchHit>();
            if (used.Count > 0) foreach (var n in used) list.Add(hits[n - 1]);
            else for (int i = 0; i < hits.Count && i < 5; i++) list.Add(hits[i]);
            return list;
        }

        // ================= Claude =================

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

        // ================= OpenAI 兼容 / DeepSeek =================

        static JsonObject OpenAiBody(string model, string system, IList<ChatMsg> history, bool deepseek, bool think)
        {
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
            return body;
        }

        static HttpRequestMessage OpenAiRequest(string baseUrl, string key, JsonObject body)
        {
            var url = baseUrl.EndsWith("/chat/completions") ? baseUrl : baseUrl + "/chat/completions";
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return req;
        }

        /// <summary>解析一块流式数据：返回正文片段；思考内容放进 reasoning；工具调用的碎片拼进 tools</summary>
        static string ParseOpenAi(string data, out string reasoning, SortedDictionary<int, ToolCall> tools)
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

            if (tools != null && delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    int idx = tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : tools.Count;
                    if (!tools.TryGetValue(idx, out var call)) { call = new ToolCall(); tools[idx] = call; }
                    if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) call.Id = id.GetString();
                    if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) call.Name += nm.GetString();
                        if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String) call.Args.Append(ar.GetString());
                    }
                }
            }

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
            return null;
        }

        // ================= 错误说明 =================

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
                500 or 502 or 503 or 504 or 529 => "服务暂时繁忙，稍后再试",
                _ => "请求失败",
            };
            return string.IsNullOrWhiteSpace(detail) ? $"{hint}（{code}）" : $"{hint}（{code}）：{detail.Trim()}";
        }
    }
}
