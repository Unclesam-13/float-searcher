package com.huaci.ai

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.DisplayMetrics
import android.view.Display

/**
 * 用系统的「屏幕投射」接口（MediaProjection）取屏幕画面。
 *
 * 授权后虚拟屏幕一直接着 ImageReader，持续收下最新一帧（画面不变时系统不会出新帧，几乎不耗电）。
 * 截屏时只取「悬浮窗移走之后」到达的那一帧，保证截图里没有自己的浮窗。
 * 闲置一段时间自动释放授权。
 */
class ScreenCapture(
    private val ctx: Context,
    private val onStopped: () -> Unit,
) {
    private val main = Handler(Looper.getMainLooper())
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var readerW = 0
    private var readerH = 0

    // 最新收到的一帧及其到达时间
    private var latest: Image? = null
    private var latestAt = 0L

    // 进行中的一次截屏
    private var pending: ((Bitmap?) -> Unit)? = null
    private var grabToken = 0
    private val releaseLater = Runnable { release() }

    val active: Boolean get() = projection != null

    /** 用授权结果建立投射。调用前服务必须已经以 mediaProjection 类型运行在前台。 */
    fun start(resultCode: Int, data: Intent): Boolean {
        release()
        val mpm = ctx.getSystemService(MediaProjectionManager::class.java)
        val p = try { mpm.getMediaProjection(resultCode, data) } catch (_: Exception) { null } ?: return false
        projection = p
        // Android 14 起必须先注册回调，才能创建虚拟屏幕。回调已在主线程，直接清理。
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() { cleanup() }
        }, main)

        val (w, h, dpi) = screenSize()
        val r = newReader(w, h)
        display = try {
            p.createVirtualDisplay(
                "huaci-capture", w, h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                r.surface, null, main
            )
        } catch (_: Exception) { null }
        if (display == null) { release(); return false }
        keepAlive()
        return true
    }

    private fun newReader(w: Int, h: Int): ImageReader {
        val r = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 3)
        r.setOnImageAvailableListener({ rd ->
            try {
                val img = rd.acquireLatestImage() ?: return@setOnImageAvailableListener
                latest?.close()
                latest = img
                latestAt = SystemClock.uptimeMillis()
            } catch (_: Exception) {}
        }, main)
        reader = r; readerW = w; readerH = h
        return r
    }

    /**
     * 截一帧。调用前先把自己的悬浮窗都移走。
     * @param hiddenAt 悬浮窗移走的时刻（SystemClock.uptimeMillis）；只接受这之后到达的画面
     * @param settleMs 至少等多久（让键盘、弹窗的消失动画走完）
     */
    fun grab(hiddenAt: Long, settleMs: Long, callback: (Bitmap?) -> Unit) {
        val vd = display ?: return callback(null)
        val token = ++grabToken
        pending = callback
        keepAlive()

        // 屏幕转过：调整虚拟屏幕和接收器尺寸
        val (w, h, dpi) = screenSize()
        if (w != readerW || h != readerH) {
            val old = reader
            val r = newReader(w, h)
            try { vd.resize(w, h, dpi); vd.surface = r.surface } catch (_: Exception) {}
            try { latest?.close() } catch (_: Exception) {}
            latest = null; latestAt = 0
            try { old?.setOnImageAvailableListener(null, null); old?.close() } catch (_: Exception) {}
        }

        val start = SystemClock.uptimeMillis()
        fun check() {
            if (token != grabToken) return
            val now = SystemClock.uptimeMillis()
            val waited = now - start
            val fresh = latest != null && latestAt >= hiddenAt + 60
            // 有新画面，且距最后一帧已稳定 120ms（动画走完）→ 取它
            if (waited >= settleMs && fresh && now - latestAt >= 120) return deliver(token)
            // 等了 1.5 秒：画面一直在动就取最新的；完全没有新帧（屏幕静止）就取现有的那帧
            if (waited >= settleMs + 1500) return deliver(token)
            main.postDelayed({ check() }, 40)
        }
        main.postDelayed({ check() }, 40)
    }

    private fun deliver(token: Int) {
        if (token != grabToken) return
        grabToken++
        val img = latest
        val bmp = try { img?.let { toBitmap(it) } } catch (_: Exception) { null }
        val cb = pending
        pending = null
        cb?.invoke(bmp)
    }

    private fun toBitmap(image: Image): Bitmap {
        val plane = image.planes[0]
        val buffer = plane.buffer.duplicate().apply { rewind() }
        val pixelStride = plane.pixelStride
        val rowStride = plane.rowStride
        val rowPadding = rowStride - pixelStride * image.width
        val padded = Bitmap.createBitmap(
            image.width + rowPadding / pixelStride, image.height, Bitmap.Config.ARGB_8888
        )
        padded.copyPixelsFromBuffer(buffer)
        if (padded.width == image.width) return padded
        val exact = Bitmap.createBitmap(padded, 0, 0, image.width, image.height)
        padded.recycle()
        return exact
    }

    private fun keepAlive() {
        main.removeCallbacks(releaseLater)
        main.postDelayed(releaseLater, IDLE_MS)
    }

    /** 主动结束投射（系统的投屏提示也会消失）。 */
    fun release() {
        main.removeCallbacks(releaseLater)
        val p = projection
        if (p != null) {
            try { p.stop() } catch (_: Exception) {}
        }
        cleanup()
    }

    private fun cleanup() {
        val wasActive = projection != null
        main.removeCallbacks(releaseLater)
        grabToken++
        val cb = pending
        pending = null
        try { reader?.setOnImageAvailableListener(null, null) } catch (_: Exception) {}
        try { latest?.close() } catch (_: Exception) {}
        latest = null; latestAt = 0
        try { display?.release() } catch (_: Exception) {}
        try { reader?.close() } catch (_: Exception) {}
        display = null; reader = null; projection = null
        readerW = 0; readerH = 0
        cb?.invoke(null) // 进行中的截屏：告诉调用方失败了，避免界面卡住
        if (wasActive) onStopped()
    }

    private data class Size(val w: Int, val h: Int, val dpi: Int)

    private fun screenSize(): Size {
        // 用 DisplayManager 取真实分辨率（含状态栏和导航栏），与截到的画面一致
        val dm = DisplayMetrics()
        val d = ctx.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)
        @Suppress("DEPRECATION")
        d.getRealMetrics(dm)
        return Size(dm.widthPixels, dm.heightPixels, dm.densityDpi)
    }

    companion object {
        /** 闲置 90 秒后释放投射 */
        const val IDLE_MS = 90 * 1000L
    }
}
