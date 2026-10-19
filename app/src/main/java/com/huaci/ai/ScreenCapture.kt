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
import android.util.DisplayMetrics
import android.view.Display

/**
 * 用系统的「屏幕投射」接口（MediaProjection）抓取一帧屏幕。
 *
 * - 授权一次后保持一段时间，期间再截屏不用重复确认；闲置后自动释放。
 * - 平时虚拟屏幕不接画面（surface = null），不耗电；只在截屏那一刻接上，
 *   接上会强制系统输出一帧最新画面，等画面稳定后取最后一帧。
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

    // 正在进行的一次截屏
    private var pending: ((Bitmap?) -> Unit)? = null
    private var latest: Image? = null
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
        display = try {
            // 先不接 surface：闲置时不渲染
            p.createVirtualDisplay(
                "huaci-capture", w, h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                null, null, main
            )
        } catch (_: Exception) { null }
        if (display == null) { release(); return false }
        keepAlive()
        return true
    }

    /**
     * 抓一帧。先把自己的悬浮窗都移走再调用。
     * @param settleMs 等多久再接画面（让键盘、弹窗的消失动画走完）
     */
    fun grab(settleMs: Long, callback: (Bitmap?) -> Unit) {
        if (display == null) return callback(null)
        cancelGrab()
        val token = ++grabToken
        pending = callback
        keepAlive()
        main.postDelayed({ if (token == grabToken) attachAndWait(token) }, settleMs)
    }

    private fun attachAndWait(token: Int) {
        val vd = display ?: return finishGrab(token, null)
        val (w, h, dpi) = screenSize()
        if (reader == null || w != readerW || h != readerH) {
            reader?.close()
            if (w != readerW || h != readerH) vd.resize(w, h, dpi)
            reader = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 3)
            readerW = w; readerH = h
        }
        val r = reader ?: return finishGrab(token, null)

        // 每来一帧就留下最新的；150ms 内没有新帧就认为画面稳定了
        val settled = Runnable { finishGrab(token, null) }
        r.setOnImageAvailableListener({ rd ->
            if (token != grabToken) return@setOnImageAvailableListener
            try {
                latest?.close()
                latest = rd.acquireLatestImage()
            } catch (_: Exception) {}
            main.removeCallbacks(settled)
            main.postDelayed(settled, 150)
        }, main)
        vd.surface = r.surface
        // 最多等 1.2 秒
        main.postDelayed({ if (token == grabToken) finishGrab(token, null) }, 1200)
    }

    private fun finishGrab(token: Int, ignored: Bitmap?) {
        if (token != grabToken) return
        grabToken++ // 之后的回调都作废
        val img = latest
        latest = null
        val bmp = try { img?.let { toBitmap(it) } } catch (_: Exception) { null } ?: ignored
        try { img?.close() } catch (_: Exception) {}
        detach()
        val cb = pending
        pending = null
        cb?.invoke(bmp)
    }

    private fun detach() {
        try { reader?.setOnImageAvailableListener(null, null) } catch (_: Exception) {}
        try { display?.surface = null } catch (_: Exception) {}
    }

    private fun cancelGrab() {
        grabToken++
        try { latest?.close() } catch (_: Exception) {}
        latest = null
        detach()
    }

    private fun toBitmap(image: Image): Bitmap {
        val plane = image.planes[0]
        val pixelStride = plane.pixelStride
        val rowStride = plane.rowStride
        val rowPadding = rowStride - pixelStride * image.width
        val padded = Bitmap.createBitmap(
            image.width + rowPadding / pixelStride, image.height, Bitmap.Config.ARGB_8888
        )
        padded.copyPixelsFromBuffer(plane.buffer)
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
        // 进行中的截屏：告诉调用方失败了，避免界面卡在截屏状态
        val cb = pending
        pending = null
        cancelGrab()
        try { display?.release() } catch (_: Exception) {}
        try { reader?.close() } catch (_: Exception) {}
        display = null; reader = null; projection = null
        readerW = 0; readerH = 0
        cb?.invoke(null)
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
        /** 闲置 2 分钟后释放投射 */
        const val IDLE_MS = 2 * 60 * 1000L
    }
}
