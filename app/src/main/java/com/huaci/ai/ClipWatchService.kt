package com.huaci.ai

import android.accessibilityservice.AccessibilityService
import android.os.SystemClock
import android.view.accessibility.AccessibilityEvent

/**
 * 无障碍服务：只用来发现「用户刚复制了东西」。
 *
 * 只看三种事件的文字：点了写着「复制 / Copy」的按钮、弹出「已复制」之类的提示、系统的剪贴板浮层。
 * 不读取屏幕内容（canRetrieveWindowContent = false），也不读剪贴板本身——
 * 发现复制后交给悬浮球服务，由它去读。
 */
class ClipWatchService : AccessibilityService() {

    private var lastHit = 0L

    override fun onAccessibilityEvent(e: AccessibilityEvent?) {
        e ?: return
        val pkg = e.packageName?.toString().orEmpty()
        if (pkg == packageName) return // 自己浮窗里的「复制」不算

        val texts = ArrayList<String>(4)
        e.text?.forEach { if (it != null) texts.add(it.toString()) }
        e.contentDescription?.let { texts.add(it.toString()) }

        val hit = when (e.eventType) {
            AccessibilityEvent.TYPE_VIEW_CLICKED -> texts.any(::isCopyButton)
            AccessibilityEvent.TYPE_NOTIFICATION_STATE_CHANGED,
            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED ->
                texts.any(::isCopiedMessage) ||
                    (pkg == "com.android.systemui" &&
                        e.className?.toString()?.contains("clipboard", ignoreCase = true) == true)
            else -> false
        }
        if (!hit) return

        val now = SystemClock.uptimeMillis()
        if (now - lastHit < 800) return
        lastHit = now
        FloatService.instance?.onCopyDetected()
    }

    override fun onInterrupt() {}

    /** 菜单或按钮上的「复制」「Copy」等（不超过 8 个字，避免把正文里提到“复制”的地方当成按钮） */
    private fun isCopyButton(t: String): Boolean {
        val s = t.trim()
        if (s.isEmpty() || s.length > 8) return false
        return s.contains("复制") || s.contains("拷贝") || s.contains("複製") ||
            s.equals("copy", ignoreCase = true) || s.startsWith("Copy ") || s.startsWith("copy ")
    }

    /** 「已复制」「复制成功」「Copied to clipboard」之类的提示 */
    private fun isCopiedMessage(t: String): Boolean {
        val s = t.trim()
        if (s.isEmpty() || s.length > 40) return false
        return s.contains("已复制") || s.contains("复制成功") || s.contains("已拷贝") ||
            s.contains("已複製") || s.contains("copied", ignoreCase = true)
    }
}
