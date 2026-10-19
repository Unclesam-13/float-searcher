package com.huaci.ai

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.Looper
import android.util.DisplayMetrics

/**
 * 用系统的「屏幕录制/投射」接口（MediaProjection）抓取一帧屏幕。
 * 授权一次后保持一段时间，期间再截屏不用重复确认；闲置几分钟后自动释放。
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
    private val releaseLater = Runnable { release() }

    val active: Boolean get() = projection != null

    /** 用授权结果建立投射。调用前服务必须已经以 mediaProjection 类型运行在前台。 */
    fun start(resultCode: Int, data: Intent): Boolean {
        release()
        val mpm = ctx.getSystemService(MediaProjectionManager::class.java)
        val p = try { mpm.getMediaProjection(resultCode, data) } catch (_: Exception) { null } ?: return false
        projection = p
        // Android 14 起必须先注册回调，才能创建虚拟屏幕
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() { main.post { cleanup() } }
        }, main)

        val (w, h, dpi) = screenSize()
        val r = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2)
        reader = r; readerW = w; readerH = h
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

    /** 抓一帧。先把自己的悬浮窗都移走再调用。 */
    fun grab(callback: (Bitmap?) -> Unit) {
        val vd = display ?: return callback(null)
        // 屏幕旋转过：调整虚拟屏幕尺寸
        val (w, h, dpi) = screenSize()
        if (w != readerW || h != readerH) {
            val old = reader
            val r = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2)
            vd.resize(w, h, dpi)
            vd.surface = r.surface
            reader = r; readerW = w; readerH = h
            old?.close()
        }
        keepAlive()
        // 丢掉旧帧，等悬浮窗消失后的新画面
        try { reader?.acquireLatestImage()?.close() } catch (_: Exception) {}
        tryGrab(callback, attempt = 0)
    }

    private fun tryGrab(callback: (Bitmap?) -> Unit, attempt: Int) {
        main.postDelayed({
            val bmp = try { readFrame() } catch (_: Exception) { null }
            when {
                bmp != null -> callback(bmp)
                attempt < 12 -> tryGrab(callback, attempt + 1)
                else -> callback(null)
            }
        }, if (attempt == 0) 280L else 90L)
    }

    private fun readFrame(): Bitmap? {
        val image = reader?.acquireLatestImage() ?: return null
        image.use {
            val plane = it.planes[0]
            val pixelStride = plane.pixelStride
            val rowStride = plane.rowStride
            val rowPadding = rowStride - pixelStride * it.width
            val padded = Bitmap.createBitmap(
                it.width + rowPadding / pixelStride, it.height, Bitmap.Config.ARGB_8888
            )
            padded.copyPixelsFromBuffer(plane.buffer)
            if (padded.width == it.width) return padded
            val exact = Bitmap.createBitmap(padded, 0, 0, it.width, it.height)
            padded.recycle()
            return exact
        }
    }

    private fun keepAlive() {
        main.removeCallbacks(releaseLater)
        main.postDelayed(releaseLater, IDLE_MS)
    }

    /** 主动结束投射（系统的录屏提示也会消失）。 */
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
        try { display?.release() } catch (_: Exception) {}
        try { reader?.close() } catch (_: Exception) {}
        display = null; reader = null; projection = null
        readerW = 0; readerH = 0
        if (wasActive) onStopped()
    }

    private data class Size(val w: Int, val h: Int, val dpi: Int)

    private fun screenSize(): Size {
        // 用 DisplayManager 取真实分辨率（含状态栏和导航栏），与截到的画面一致
        val dm = DisplayMetrics()
        val d = ctx.getSystemService(DisplayManager::class.java).getDisplay(android.view.Display.DEFAULT_DISPLAY)
        @Suppress("DEPRECATION")
        d.getRealMetrics(dm)
        return Size(dm.widthPixels, dm.heightPixels, dm.densityDpi)
    }

    companion object {
        /** 闲置 3 分钟后释放投射 */
        const val IDLE_MS = 3 * 60 * 1000L
    }
}
