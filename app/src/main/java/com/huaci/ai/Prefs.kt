package com.huaci.ai

import android.content.Context
import android.content.SharedPreferences

data class Preset(val name: String, val prompt: String)

/** 所有设置都存在本机 SharedPreferences 里。 */
object Prefs {
    const val ANTHROPIC = "anthropic"
    const val OPENAI = "openai"

    val DEFAULT_PRESETS = """
        解释 | 用通俗的话解释这段文字是什么意思，必要时补充背景。
        翻译成中文 | 把这段文字翻译成自然流畅的中文，只给译文。
        翻译成英文 | 把这段文字翻译成地道的英文，只给译文。
        总结 | 用三句话以内总结这段文字的要点。
        列要点 | 把这段文字整理成简短的要点列表。
        润色 | 在不改变原意的前提下润色这段文字，直接给出改好的版本。
        举例 | 针对这段文字里的核心概念，举一两个具体例子帮我理解。
        挑毛病 | 指出这段文字里的事实错误、逻辑漏洞或值得怀疑的说法。
        怎么回复 | 这是别人发给我的消息，帮我写两三个得体的回复，口吻自然。
    """.trimIndent()

    private fun sp(c: Context): SharedPreferences =
        c.getSharedPreferences("huaci", Context.MODE_PRIVATE)

    fun provider(c: Context): String = sp(c).getString("provider", ANTHROPIC) ?: ANTHROPIC
    fun baseUrl(c: Context): String = sp(c).getString("baseUrl", null) ?: defaultBase(provider(c))
    fun apiKey(c: Context): String = sp(c).getString("apiKey", "") ?: ""
    fun model(c: Context): String = sp(c).getString("model", null) ?: defaultModel(provider(c))
    fun presetsText(c: Context): String = sp(c).getString("presets", null) ?: DEFAULT_PRESETS
    fun autoRun(c: Context): Boolean = sp(c).getBoolean("autoRun", false)

    fun save(
        c: Context, provider: String, baseUrl: String, apiKey: String,
        model: String, presets: String, autoRun: Boolean
    ) {
        sp(c).edit()
            .putString("provider", provider)
            .putString("baseUrl", baseUrl.trim())
            .putString("apiKey", apiKey.trim())
            .putString("model", model.trim())
            .putString("presets", presets)
            .putBoolean("autoRun", autoRun)
            .apply()
    }

    fun bubblePos(c: Context): Pair<Int, Int> =
        sp(c).getInt("bx", -1) to sp(c).getInt("by", -1)

    fun saveBubblePos(c: Context, x: Int, y: Int) {
        sp(c).edit().putInt("bx", x).putInt("by", y).apply()
    }

    fun defaultBase(provider: String) =
        if (provider == ANTHROPIC) "https://api.anthropic.com" else "https://api.openai.com/v1"

    fun defaultModel(provider: String) =
        if (provider == ANTHROPIC) "claude-sonnet-5-5" else ""

    /** 每行「按钮名 | 指令」，竖线可用半角或全角。没写竖线时整行既当名字又当指令。 */
    fun presets(c: Context): List<Preset> =
        presetsText(c).lines().mapNotNull { raw ->
            val line = raw.trim()
            if (line.isEmpty()) return@mapNotNull null
            val idx = line.indexOfFirst { it == '|' || it == '｜' }
            if (idx > 0) {
                val name = line.substring(0, idx).trim()
                val prompt = line.substring(idx + 1).trim()
                if (name.isEmpty() || prompt.isEmpty()) null else Preset(name, prompt)
            } else {
                Preset(if (line.length > 6) line.take(6) + "…" else line, line)
            }
        }

    fun buildPrompt(p: Preset, text: String): String =
        if (p.prompt.contains("{text}")) p.prompt.replace("{text}", text)
        else "${p.prompt}\n\n下面是选中的文字：\n\"\"\"\n$text\n\"\"\""
}
