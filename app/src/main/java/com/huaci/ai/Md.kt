package com.huaci.ai

import android.graphics.Typeface
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.style.RelativeSizeSpan
import android.text.style.StyleSpan
import android.text.style.TypefaceSpan

/** 极简 Markdown 渲染：标题、列表符号、**粗体**、`代码`。够在小浮窗里读就行。 */
object Md {
    fun render(src: String): CharSequence {
        val out = SpannableStringBuilder()
        val lines = src.split("\n")
        lines.forEachIndexed { i, raw ->
            val t = raw.trimStart()
            var line = raw
            var heading = false
            when {
                t.startsWith("#") -> { line = t.trimStart('#').trimStart(); heading = true }
                t.startsWith("- ") || t.startsWith("* ") -> line = raw.substring(0, raw.length - t.length) + "• " + t.substring(2)
                t == "---" || t == "***" -> line = ""
            }
            val start = out.length
            appendInline(out, line)
            if (heading && out.length > start) {
                out.setSpan(StyleSpan(Typeface.BOLD), start, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                out.setSpan(RelativeSizeSpan(1.06f), start, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            }
            if (i < lines.size - 1) out.append('\n')
        }
        return out
    }

    private fun appendInline(out: SpannableStringBuilder, s: String) {
        var i = 0
        while (i < s.length) {
            if (s.startsWith("**", i)) {
                val end = s.indexOf("**", i + 2)
                if (end > i + 2) {
                    val st = out.length
                    out.append(s, i + 2, end)
                    out.setSpan(StyleSpan(Typeface.BOLD), st, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                    i = end + 2
                    continue
                }
            }
            if (s[i] == '`') {
                val end = s.indexOf('`', i + 1)
                if (end > i + 1) {
                    val st = out.length
                    out.append(s, i + 1, end)
                    out.setSpan(TypefaceSpan("monospace"), st, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                    i = end + 1
                    continue
                }
            }
            out.append(s[i])
            i++
        }
    }
}
