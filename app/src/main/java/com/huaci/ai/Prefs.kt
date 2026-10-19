package com.huaci.ai

import android.content.Context
import android.content.SharedPreferences

data class Preset(val name: String, val prompt: String)

/** 所有设置都存在本机 SharedPreferences 里。 */
object Prefs {
    const val ANTHROPIC = "anthropic"
    const val OPENAI = "openai"
    const val DEEPSEEK = "deepseek"
    val ALL = listOf(ANTHROPIC, DEEPSEEK, OPENAI)

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
    /** DeepSeek 深度思考：默认关，划词场景要快 */
    fun deepThink(c: Context): Boolean = sp(c).getBoolean("deepThink", false)

    fun save(
        c: Context, provider: String, baseUrl: String, apiKey: String,
        model: String, presets: String, autoRun: Boolean, deepThink: Boolean
    ) {
        sp(c).edit()
            .putString("provider", provider)
            .putString("baseUrl", baseUrl.trim())
            .putString("apiKey", apiKey.trim())
            .putString("model", model.trim())
            .putString("presets", presets)
            .putBoolean("autoRun", autoRun)
            .putBoolean("deepThink", deepThink)
            .apply()
    }

    /** 浮窗大小：横屏、竖屏分别记住；0 表示用默认值 */
    fun panelSize(c: Context, landscape: Boolean): Pair<Int, Int> {
        val k = if (landscape) "L" else "P"
        return sp(c).getInt("pw$k", 0) to sp(c).getInt("ph$k", 0)
    }

    fun savePanelSize(c: Context, landscape: Boolean, w: Int, h: Int) {
        val k = if (landscape) "L" else "P"
        sp(c).edit().putInt("pw$k", w).putInt("ph$k", h).apply()
    }

    /** 识字取景框大小；0 表示用默认值 */
    fun scanSize(c: Context): Pair<Int, Int> = sp(c).getInt("sw", 0) to sp(c).getInt("sh", 0)
    fun saveScanSize(c: Context, w: Int, h: Int) { sp(c).edit().putInt("sw", w).putInt("sh", h).apply() }

    /** 浮窗不透明度 0.25 ~ 1.0 */
    fun panelAlpha(c: Context): Float = sp(c).getFloat("alpha", 1f).coerceIn(0.25f, 1f)
    fun savePanelAlpha(c: Context, a: Float) { sp(c).edit().putFloat("alpha", a).apply() }

    fun bubblePos(c: Context): Pair<Int, Int> =
        sp(c).getInt("bx", -1) to sp(c).getInt("by", -1)

    fun saveBubblePos(c: Context, x: Int, y: Int) {
        sp(c).edit().putInt("bx", x).putInt("by", y).apply()
    }

    fun defaultBase(provider: String) = when (provider) {
        ANTHROPIC -> "https://api.anthropic.com"
        DEEPSEEK -> "https://api.deepseek.com"
        else -> "https://api.openai.com/v1"
    }

    fun defaultModel(provider: String) = when (provider) {
        ANTHROPIC -> "claude-sonnet-5-5"
        DEEPSEEK -> "deepseek-flash"
        else -> ""
    }

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

    /** 有截图时额外出现的第一个按钮 */
    val OCR_PRESET = Preset("提取文字", "把截图里的文字原样提取出来，保持原有的分段和顺序，不要解释。")

    /** 截图提问：图片随消息一起发送，这里只给文字指令。 */
    fun buildImagePrompt(p: Preset): String =
        if (p.prompt.contains("{text}")) p.prompt.replace("{text}", "截图中的内容")
        else "${p.prompt}\n\n（要处理的内容在附带的截图里。）"

    fun buildPrompt(p: Preset, text: String): String =
        if (p.prompt.contains("{text}")) p.prompt.replace("{text}", text)
        else "${p.prompt}\n\n下面是选中的文字：\n\"\"\"\n$text\n\"\"\""
}
