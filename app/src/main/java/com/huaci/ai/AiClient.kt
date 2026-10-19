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

        Thread {
            var c: HttpURLConnection? = null
            try {
                c = if (provider == Prefs.ANTHROPIC) openAnthropic(base, key, model, history)
                    else openOpenAi(base, key, model, history, provider)
                conn = c
                val code = c.responseCode
                if (code !in 200..299) {
                    val body = try { c.errorStream?.bufferedReader()?.readText().orEmpty() } catch (_: Exception) { "" }
                    val hasImage = history.any { it.image != null }
                    ui { onError(describeHttp(code, body, hasImage)) }
                    return@Thread
                }
                var thought = 0
                val onReason: (String) -> Unit = { r ->
                    thought += r.length
                    val n = thought
                    if (onThinking != null) ui { onThinking(n) }
                }
                c.inputStream.bufferedReader(Charsets.UTF_8).use { reader ->
                    while (alive()) {
                        val line = reader.readLine() ?: break
                        if (!line.startsWith("data:")) continue
                        val data = line.substring(5).trim()
                        if (data.isEmpty()) continue
                        if (data == "[DONE]") break
                        val piece = if (provider == Prefs.ANTHROPIC) parseAnthropic(data) else parseOpenAi(data, onReason)
                        if (!piece.isNullOrEmpty()) ui { onDelta(piece) }
                    }
                }
                ui { onDone() }
            } catch (e: UnknownHostException) {
                ui { onError("连不上服务器：请检查网络，或接口地址是否写对。") }
            } catch (e: SocketTimeoutException) {
                ui { onError("等太久没有回应，请稍后再试。") }
            } catch (e: Exception) {
                ui { onError(e.message ?: e.javaClass.simpleName) }
            } finally {
                try { c?.disconnect() } catch (_: Exception) {}
                if (conn === c) conn = null
            }
        }.start()
    }

    // ---------- Claude ----------

    private fun openAnthropic(base: String, key: String, model: String, history: List<Msg>): HttpURLConnection {
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
            .put("system", SYSTEM_PROMPT)
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

    private fun openOpenAi(base: String, key: String, model: String, history: List<Msg>, provider: String): HttpURLConnection {
        val url = if (base.endsWith("/chat/completions")) base else "$base/chat/completions"
        val msgs = JSONArray()
        msgs.put(JSONObject().put("role", "system").put("content", SYSTEM_PROMPT))
        history.forEach { m ->
            val content: Any = if (m.image == null) m.content else JSONArray()
                .put(JSONObject().put("type", "text").put("text", m.content))
                .put(JSONObject().put("type", "image_url").put("image_url",
                    JSONObject().put("url", "data:image/jpeg;base64," + m.image)))
            msgs.put(JSONObject().put("role", m.role).put("content", content))
        }
        val body = JSONObject()
            .put("model", model)
            .put("stream", true)
            .put("messages", msgs)
        if (provider == Prefs.DEEPSEEK) {
            // DeepSeek 默认开启思考模式；划词默认关掉以求快，设置里可打开
            val on = Prefs.deepThink(ctx)
            body.put("thinking", JSONObject().put("type", if (on) "enabled" else "disabled"))
        }
        return httpPost(url, body, mapOf("Authorization" to "Bearer $key"))
    }

    private fun parseOpenAi(data: String, onReason: (String) -> Unit): String? {
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
            500, 502, 503, 529 -> "服务暂时繁忙，稍后再试"
            else -> "请求失败"
        }
        return if (detail.isBlank()) "$hint（$code）" else "$hint（$code）：$detail"
    }

    companion object {
        const val SYSTEM_PROMPT =
            "你是嵌在手机悬浮窗里的划词助手。用户在其他 App 里选中了一段文字来问你。" +
            "回答要直接、简洁，适合在手机小屏幕上读：先给结论，再给必要的解释；" +
            "少用标题和表格，需要时用短列表。除非用户要求翻译成别的语言，否则用简体中文回答。"
    }
}
