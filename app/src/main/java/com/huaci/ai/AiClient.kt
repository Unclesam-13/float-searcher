package com.huaci.ai

import android.content.Context
import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.SocketTimeoutException
import java.net.URL
import java.net.UnknownHostException

/**
 * 流式调用 AI。支持两种接口：
 *  - Claude（Anthropic Messages API）
 *  - OpenAI 兼容的 /chat/completions（DeepSeek、通义千问、Kimi、智谱、OpenAI 等都可以）
 * 所有回调都在主线程执行。
 */
class AiClient(private val ctx: Context) {

    /** image：可选的 JPEG（base64），只放在用户消息里 */
    data class Msg(val role: String, val content: String, val image: String? = null)

    private val main = Handler(Looper.getMainLooper())
    @Volatile private var generation = 0
    @Volatile private var conn: HttpURLConnection? = null

    /** 取消当前请求。之后旧请求的回调都不会再触发。 */
    fun cancel() {
        generation++
        val c = conn
        conn = null
        Thread { try { c?.disconnect() } catch (_: Exception) {} }.start()
    }

    fun stream(
        history: List<Msg>,
        onDelta: (String) -> Unit,
        onDone: () -> Unit,
        onError: (String) -> Unit,
        onThinking: ((Int) -> Unit)? = null, // 深度思考时回报已思考的字数
        system: String = SYSTEM_PROMPT,       // 换成别的角色（比如翻译器）
        noThinking: Boolean = false,          // 强制关闭深度思考（求快）
        web: Boolean = false,                 // DeepSeek 联网搜索
        onSearch: ((String) -> Unit)? = null, // 开始搜索某个关键词
        onReset: (() -> Unit)? = null,        // 之前流出的文字作废（模型决定先去搜）
        onSources: ((List<SearchHit>) -> Unit)? = null, // 回答引用的来源（在 onDone 之前）
    ) {
        cancel()
        val myGen = generation
        fun alive() = myGen == generation
        fun ui(block: () -> Unit) = main.post { if (alive()) block() }

        val provider = Prefs.provider(ctx)
        val key = Prefs.apiKey(ctx)
        var model = Prefs.model(ctx)
        // DeepSeek 只有 deepseek-flash 能看图：带截图时自动用它
        if (provider == Prefs.DEEPSEEK && history.any { it.image != null } && model != "deepseek-flash") {
            model = "deepseek-flash"
        }
        val base = Prefs.baseUrl(ctx).trim().trimEnd('/')

        if (key.isBlank()) { ui { onError("还没填 API Key，请打开「划词助手」App 设置。") }; return }
        if (model.isBlank()) { ui { onError("还没填模型名，请打开「划词助手」App 设置。") }; return }

        val anthropic = provider == Prefs.ANTHROPIC
        val hasImage = history.any { it.image != null }
        Thread {
            try {
                var thought = 0
                val onReason: (String) -> Unit = { r ->
                    thought += r.length
                    val n = thought
                    if (onThinking != null) ui { onThinking(n) }
                }
                val emit: (String) -> Unit = { p -> ui { onDelta(p) } }
                if (web && provider == Prefs.DEEPSEEK) {
                    val sources = webLoop(
                        base, key, model, history, system, { alive() }, emit,
                        onSearch = { q -> ui { onSearch?.invoke(q) } },
                        onReset = { ui { onReset?.invoke() } },
                    )
                    if (!alive()) return@Thread
                    ui { onSources?.invoke(sources) }
                } else {
                    val c = if (anthropic) openAnthropic(base, key, model, history, system)
                        else openOpenAi(base, key, model, history, provider, system, noThinking)
                    readRound(c, anthropic, { alive() }, emit, onReason, null, hasImage)
                }
                ui { onDone() }
            } catch (e: HttpFail) {
                ui { onError(e.message ?: "请求失败") }
            } catch (e: UnknownHostException) {
                ui { onError("连不上服务器：请检查网络，或接口地址是否写对。") }
            } catch (e: SocketTimeoutException) {
                ui { onError("等太久没有回应，请稍后再试。") }
            } catch (e: Exception) {
                ui { onError(e.message ?: e.javaClass.simpleName) }
            }
        }.start()
    }

    // ---------- 一轮请求：读流式结果 ----------

    private class ToolCall {
        var id = ""
        var name = ""
        val args = StringBuilder()
    }

    private class Round(val content: String, val reasoning: String, val calls: List<ToolCall>)

    /** 接口返回错误码：消息就是给用户看的说明 */
    private class HttpFail(msg: String) : IOException(msg)

    private fun readRound(
        c: HttpURLConnection, anthropic: Boolean, alive: () -> Boolean,
        emit: (String) -> Unit, onReason: (String) -> Unit,
        tools: MutableMap<Int, ToolCall>?, hasImage: Boolean,
    ): Round {
        conn = c
        try {
            val code = c.responseCode
            if (code !in 200..299) {
                val body = try { c.errorStream?.bufferedReader()?.readText().orEmpty() } catch (_: Exception) { "" }
                throw HttpFail(describeHttp(code, body, hasImage))
            }
            val sb = StringBuilder()
            val think = StringBuilder()
            c.inputStream.bufferedReader(Charsets.UTF_8).use { reader ->
                while (alive()) {
                    val line = reader.readLine() ?: break
                    if (!line.startsWith("data:")) continue
                    val data = line.substring(5).trim()
                    if (data.isEmpty()) continue
                    if (data == "[DONE]") break
                    val piece = try {
                        if (anthropic) parseAnthropic(data)
                        else parseOpenAi(data, { r -> think.append(r); onReason(r) }, tools)
                    } catch (_: org.json.JSONException) { null } // 偶尔有不完整的行
                    if (!piece.isNullOrEmpty()) {
                        sb.append(piece)
                        emit(piece)
                    }
                }
            }
            val calls = tools?.toSortedMap()?.values?.toList() ?: emptyList()
            return Round(sb.toString(), think.toString(), calls)
        } finally {
            try { c.disconnect() } catch (_: Exception) {}
            if (conn === c) conn = null
        }
    }

    // ---------- DeepSeek 联网 ----------

    /** 一条搜索结果；index 在整次回答里唯一，回答里用 [index] 引用 */
    data class SearchHit(
        val index: Int, val title: String, val url: String,
        val site: String, val date: String, val summary: String,
    )

    private fun searchTool(): JSONObject = JSONObject()
        .put("type", "function")
        .put("function", JSONObject()
            .put("name", "web_search")
            .put("description", "联网搜索网页，返回编号、标题、网址、发布日期和摘要。需要最新信息、新闻、价格、版本、具体数据或核实事实时使用。")
            .put("parameters", JSONObject()
                .put("type", "object")
                .put("properties", JSONObject().put("query", JSONObject()
                    .put("type", "string")
                    .put("description", "搜索关键词，简短明确，不超过 60 个字。可以用 site:、after:2025、lang:zh,en 等语法。")))
                .put("required", JSONArray().put("query"))))

    private fun webNote(): String =
        "\n\n你可以使用 web_search 工具联网搜索。涉及最新动态、新闻、价格、版本、天气、具体数据，或你不确定的事实时，先搜索再回答；" +
        "常识和不需要时效的问题不必搜索。最多搜索 3 次。" +
        "回答时，在用到搜索结果的句子末尾标注来源编号，例如 [1] 或 [2][3]，编号和搜索结果里的一致；不要在末尾另列来源清单。" +
        "今天是 " + java.text.SimpleDateFormat("yyyy年M月d日", java.util.Locale.CHINA).format(java.util.Date()) + "。"

    /** 模型自己决定搜不搜、搜什么；最多 3 轮搜索，第 4 轮不给工具，必须作答 */
    private fun webLoop(
        base: String, key: String, model: String, history: List<Msg>, system: String,
        alive: () -> Boolean, emit: (String) -> Unit,
        onSearch: (String) -> Unit, onReset: () -> Unit,
    ): List<SearchHit> {
        val msgs = openAiMessages(history, system + webNote())
        val question = history.lastOrNull { it.role == "user" }?.content.orEmpty()
        val hasImage = history.any { it.image != null }
        val hits = ArrayList<SearchHit>()
        var round = 0
        while (true) {
            if (!alive()) return emptyList()
            val allowTools = round < 3
            val body = JSONObject()
                .put("model", model)
                .put("stream", true)
                .put("messages", JSONArray(msgs.toString()))
                // 联网时关闭深度思考：带工具的思考模式要求每次都回传思考内容，容易出错
                .put("thinking", JSONObject().put("type", "disabled"))
            if (allowTools) body.put("tools", JSONArray().put(searchTool()))
            val tools = if (allowTools) HashMap<Int, ToolCall>() else null
            val c = httpPost(chatUrl(base), body, mapOf("Authorization" to "Bearer $key"))
            val r = readRound(c, false, alive, emit, {}, tools, hasImage)
            if (!alive()) return emptyList()

            val calls = r.calls.filter { it.name == "web_search" }
            if (calls.isEmpty()) return cited(r.content, hits)

            onReset() // 前面流出来的「我来搜一下」不显示
            calls.forEachIndexed { i, tc -> if (tc.id.isEmpty()) tc.id = "call_${round}_$i" }
            val tcArr = JSONArray()
            calls.forEach { tc ->
                tcArr.put(JSONObject().put("id", tc.id).put("type", "function")
                    .put("function", JSONObject().put("name", tc.name).put("arguments", tc.args.toString())))
            }
            val asst = JSONObject().put("role", "assistant").put("content", r.content).put("tool_calls", tcArr)
            if (r.reasoning.isNotEmpty()) asst.put("reasoning_content", r.reasoning)
            msgs.put(asst)

            for (tc in calls) {
                val q = queryFrom(tc.args.toString())
                val text = if (q.isEmpty()) "搜索关键词为空。" else {
                    onSearch(q)
                    try {
                        val found = search(base, key, q, question).mapIndexed { i, h -> h.copy(index = hits.size + 1 + i) }
                        hits.addAll(found)
                        formatHits(q, found)
                    } catch (e: Exception) {
                        "搜索失败：${e.message}。请根据已有知识回答，并说明没能联网核实。"
                    }
                }
                msgs.put(JSONObject().put("role", "tool").put("tool_call_id", tc.id).put("content", text))
            }
            round++
        }
    }

    private fun queryFrom(args: String): String = try {
        JSONObject(args.ifBlank { "{}" }).optString("query").trim()
    } catch (_: Exception) { "" }

    /** DeepSeek 搜索接口：POST /v1/tools/search */
    private fun search(base: String, key: String, query: String, question: String): List<SearchHit> {
        val root = if (base.endsWith("/v1")) base.dropLast(3) else base
        val body = JSONObject().put("query", query.take(128)).put("count", 6)
        val oq = question.replace('\n', ' ').trim().take(128)
        if (oq.isNotEmpty()) body.put("ori_question", oq)
        val c = URL("$root/v1/tools/search").openConnection() as HttpURLConnection
        try {
            c.requestMethod = "POST"
            c.doOutput = true
            c.connectTimeout = 15_000
            c.readTimeout = 25_000
            c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
            c.setRequestProperty("Authorization", "Bearer $key")
            c.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            val code = c.responseCode
            val text = (if (code in 200..299) c.inputStream else c.errorStream)
                ?.bufferedReader(Charsets.UTF_8)?.readText().orEmpty()
            if (code !in 200..299) throw IOException(describeHttp(code, text))
            val arr = JSONObject(text).optJSONArray("results") ?: return emptyList()
            val out = ArrayList<SearchHit>()
            for (i in 0 until arr.length()) {
                val o = arr.optJSONObject(i) ?: continue
                val url = o.optString("url")
                if (url.isBlank()) continue
                fun str(n: String) = if (o.isNull(n)) "" else o.optString(n)
                val summary = str("summary").ifBlank { str("snippet") }.take(800)
                out.add(SearchHit(0, str("title"), url, str("site_name"), str("date"), summary))
            }
            return out
        } finally {
            try { c.disconnect() } catch (_: Exception) {}
        }
    }

    private fun formatHits(query: String, hits: List<SearchHit>): String {
        if (hits.isEmpty()) return "搜索「$query」没有找到结果。"
        val sb = StringBuilder("搜索「$query」的结果：\n")
        for (h in hits) {
            sb.append("\n[").append(h.index).append("] ").append(h.title)
            if (h.site.isNotEmpty()) sb.append(" — ").append(h.site)
            if (h.date.length >= 10) sb.append(" · ").append(h.date.take(10))
            sb.append('\n').append(h.url).append('\n').append(h.summary).append('\n')
        }
        return sb.toString()
    }

    /** 回答里引用了哪些 [编号]；一个都没引用时给前 5 条 */
    private fun cited(answer: String, hits: List<SearchHit>): List<SearchHit> {
        val used = Regex("""\[(\d{1,2})]""").findAll(answer)
            .mapNotNull { it.groupValues[1].toIntOrNull() }
            .filter { it in 1..hits.size }
            .toSortedSet()
        return if (used.isNotEmpty()) used.map { hits[it - 1] } else hits.take(5)
    }

    // ---------- Claude ----------

    private fun openAnthropic(base: String, key: String, model: String, history: List<Msg>, system: String): HttpURLConnection {
        val url = if (base.endsWith("/v1")) "$base/messages" else "$base/v1/messages"
        val msgs = JSONArray()
        history.forEach { m ->
            val content: Any = if (m.image == null) m.content else JSONArray()
                .put(JSONObject().put("type", "image").put("source", JSONObject()
                    .put("type", "base64").put("media_type", "image/jpeg").put("data", m.image)))
                .put(JSONObject().put("type", "text").put("text", m.content))
            msgs.put(JSONObject().put("role", m.role).put("content", content))
        }
        val body = JSONObject()
            .put("model", model)
            .put("max_tokens", 2048)
            .put("stream", true)
            .put("system", system)
            .put("messages", msgs)
        return httpPost(url, body, mapOf(
            "x-api-key" to key,
            "anthropic-version" to "2023-06-01",
        ))
    }

    private fun parseAnthropic(data: String): String? {
        val o = JSONObject(data)
        return when (o.optString("type")) {
            "content_block_delta" -> {
                val d = o.optJSONObject("delta") ?: return null
                if (d.optString("type") == "text_delta") d.optString("text") else null
            }
            "error" -> throw IOException(o.optJSONObject("error")?.optString("message") ?: "服务返回错误")
            else -> null
        }
    }

    // ---------- OpenAI 兼容 ----------

    private fun openOpenAi(
        base: String, key: String, model: String, history: List<Msg>, provider: String,
        system: String, noThinking: Boolean,
    ): HttpURLConnection {
        val url = chatUrl(base)
        val msgs = openAiMessages(history, system)
        val body = JSONObject()
            .put("model", model)
            .put("stream", true)
            .put("messages", msgs)
        if (provider == Prefs.DEEPSEEK) {
            // DeepSeek 默认开启思考模式；划词默认关掉以求快，设置里可打开
            val on = Prefs.deepThink(ctx) && !noThinking
            body.put("thinking", JSONObject().put("type", if (on) "enabled" else "disabled"))
        }
        return httpPost(url, body, mapOf("Authorization" to "Bearer $key"))
    }

    private fun chatUrl(base: String) = if (base.endsWith("/chat/completions")) base else "$base/chat/completions"

    private fun openAiMessages(history: List<Msg>, system: String): JSONArray {
        val msgs = JSONArray()
        msgs.put(JSONObject().put("role", "system").put("content", system))
        history.forEach { m ->
            val content: Any = if (m.image == null) m.content else JSONArray()
                .put(JSONObject().put("type", "text").put("text", m.content))
                .put(JSONObject().put("type", "image_url").put("image_url",
                    JSONObject().put("url", "data:image/jpeg;base64," + m.image)))
            msgs.put(JSONObject().put("role", m.role).put("content", content))
        }
        return msgs
    }

    private fun parseOpenAi(data: String, onReason: (String) -> Unit, tools: MutableMap<Int, ToolCall>? = null): String? {
        val o = JSONObject(data)
        o.optJSONObject("error")?.let { throw IOException(it.optString("message", "服务返回错误")) }
        val choices = o.optJSONArray("choices") ?: return null
        if (choices.length() == 0) return null
        val delta = choices.getJSONObject(0).optJSONObject("delta") ?: return null
        // 思考过程（DeepSeek、通义等用 reasoning_content），只计数不显示
        if (delta.has("reasoning_content") && !delta.isNull("reasoning_content")) {
            val r = delta.optString("reasoning_content")
            if (r.isNotEmpty()) onReason(r)
        }
        // 工具调用（联网搜索）分成很多碎片流过来，按 index 拼起来
        val tcs = if (tools != null) delta.optJSONArray("tool_calls") else null
        if (tools != null && tcs != null) {
            for (i in 0 until tcs.length()) {
                val tc = tcs.optJSONObject(i) ?: continue
                val idx = if (tc.has("index")) tc.optInt("index") else tools.size
                val call = tools.getOrPut(idx) { ToolCall() }
                if (tc.has("id") && !tc.isNull("id")) tc.optString("id").takeIf { it.isNotEmpty() }?.let { call.id = it }
                val fn = tc.optJSONObject("function")
                if (fn != null) {
                    if (fn.has("name") && !fn.isNull("name")) call.name += fn.optString("name")
                    if (fn.has("arguments") && !fn.isNull("arguments")) call.args.append(fn.optString("arguments"))
                }
            }
        }
        if (!delta.has("content") || delta.isNull("content")) return null
        return delta.getString("content")
    }

    // ---------- 公共 ----------

    private fun httpPost(url: String, body: JSONObject, headers: Map<String, String>): HttpURLConnection {
        val c = URL(url).openConnection() as HttpURLConnection
        c.requestMethod = "POST"
        c.doOutput = true
        c.connectTimeout = 15_000
        c.readTimeout = 120_000
        c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
        c.setRequestProperty("Accept", "text/event-stream")
        headers.forEach { (k, v) -> c.setRequestProperty(k, v) }
        c.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        return c
    }

    private fun describeHttp(code: Int, body: String, hasImage: Boolean = false): String {
        val detail = try {
            val o = JSONObject(body)
            o.optJSONObject("error")?.optString("message")?.takeIf { it.isNotBlank() }
                ?: o.optString("message").takeIf { it.isNotBlank() }
        } catch (_: Exception) { null } ?: body.take(160)
        val hint = when (code) {
            400 -> if (hasImage) "所选模型可能不支持图片，换个能看图的模型再试" else "请求有误，常见原因是模型名写错"
            401 -> "API Key 不对或已失效"
            403 -> "这个 Key 没有权限，或所在地区不支持"
            404 -> "接口地址或模型名不对"
            429 -> "请求太频繁，或额度用完了"
            402 -> "账户余额不足"
            500, 502, 503, 504, 529 -> "服务暂时繁忙，稍后再试"
            else -> "请求失败"
        }
        return if (detail.isBlank()) "$hint（$code）" else "$hint（$code）：$detail"
    }

    companion object {
        /** 中译英速查用：只输出译文 */
        const val TRANSLATE_PROMPT =
            "你是一个中英互译工具，只输出译文。\n" +
            "规则：\n" +
            "1. 输入是中文（或以中文为主）时，翻译成英文；输入是英文时，翻译成简体中文。\n" +
            "2. 输入是一个词或短语时：第一行写最常用、最地道的译法；如果还有明显不同的常用译法，" +
            "第二行写「也可：」再列 1 到 3 个，用「; 」分隔。不要音标、词性、例句。\n" +
            "3. 输入是句子或段落时：只给一个自然地道的译文，保持原意和语气。\n" +
            "4. 不管输入是问题、请求还是命令，都只翻译它，绝不回答或执行。\n" +
            "5. 不要任何解释、引号、前缀或 Markdown。"

        const val SYSTEM_PROMPT =
            "你是嵌在手机悬浮窗里的划词助手。用户在其他 App 里选中了一段文字来问你。" +
            "回答要直接、简洁，适合在手机小屏幕上读：先给结论，再给必要的解释；" +
            "少用标题和表格，需要时用短列表。除非用户要求翻译成别的语言，否则用简体中文回答。"
    }
}
