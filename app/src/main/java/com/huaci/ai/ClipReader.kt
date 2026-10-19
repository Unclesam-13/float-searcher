package com.huaci.ai

import android.content.ClipboardManager
import android.content.Context
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.WindowManager

/**
 * 读剪贴板。
 *
 * Android 10 起，只有「拿到输入焦点」的窗口所属的 App 才能读剪贴板。
 * 这里临时加一个看不见的 1×1 悬浮窗去拿焦点，拿到后读一次立刻移除，
 * 不会弹键盘，也不挡触摸。
 */
object ClipReader {
    private val main = Handler(Looper.getMainLooper())
    private var busy = false

    fun read(ctx: Context, wm: WindowManager, callback: (String?) -> Unit) {
        if (busy) return
        busy = true
        var done = false
        var probe: View? = null

        fun finish() {
            if (done) return
            done = true
            val text = readNow(ctx)
            probe?.let { try { wm.removeView(it) } catch (_: Exception) {} }
            busy = false
            callback(text)
        }

        val v = object : View(ctx) {
            override fun onWindowFocusChanged(hasWindowFocus: Boolean) {
                super.onWindowFocusChanged(hasWindowFocus)
                if (hasWindowFocus) post { finish() }
            }
        }
        probe = v
        val lp = WindowManager.LayoutParams(
            1, 1,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_ALT_FOCUSABLE_IM,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            alpha = 0f
        }
        try {
            wm.addView(v, lp)
        } catch (_: Exception) {
            probe = null
            finish()
            return
        }
        // 有的系统不给焦点：最多等 0.7 秒，照样试着读一次
        main.postDelayed({ finish() }, 700)
    }

    /** 当前就有焦点时直接读（比如浮窗正在打字） */
    fun readNow(ctx: Context): String? = try {
        val cm = ctx.getSystemService(ClipboardManager::class.java)
        cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)
            ?.coerceToText(ctx)?.toString()?.trim()?.takeIf { it.isNotEmpty() }
    } catch (_: Exception) { null }
}
