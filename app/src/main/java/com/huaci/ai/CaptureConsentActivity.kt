package com.huaci.ai

import android.app.Activity
import android.content.Intent
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle

/**
 * 透明页面：弹出系统的「允许截取屏幕」确认框，把结果交给 FloatService。
 * 授权会保留几分钟，这段时间内再截屏不会重复弹出。
 */
class CaptureConsentActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        opened = true
        if (savedInstanceState != null) return // 旋转等重建时，等待原来的结果
        val mpm = getSystemService(MediaProjectionManager::class.java)
        try {
            // Android 14+：直接请求整个屏幕，不让用户再选「单个应用」
            val req = if (Build.VERSION.SDK_INT >= 34)
                mpm.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
            else mpm.createScreenCaptureIntent()
            @Suppress("DEPRECATION")
            startActivityForResult(req, REQ)
        } catch (_: Exception) {
            sendResult(RESULT_CANCELED, null)
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ) sendResult(resultCode, data)
    }

    override fun onDestroy() {
        // 没拿到结果就被关掉（按了 Home、被系统回收）：告诉服务取消，恢复浮窗
        if (!sent && !isChangingConfigurations) deliver(RESULT_CANCELED, null)
        super.onDestroy()
    }

    private var sent = false

    private fun sendResult(resultCode: Int, data: Intent?) {
        deliver(resultCode, data)
        finish()
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }

    private fun deliver(resultCode: Int, data: Intent?) {
        if (sent) return
        sent = true
        val i = Intent(this, FloatService::class.java)
            .setAction(FloatService.ACTION_CAPTURE_RESULT)
            .putExtra(FloatService.EXTRA_RESULT_CODE, resultCode)
        if (data != null) i.putExtra(FloatService.EXTRA_RESULT_DATA, data)
        // 服务已在前台运行，普通 startService 即可；失败时退回 startForegroundService
        try { startService(i) } catch (_: Exception) {
            try { startForegroundService(i) } catch (_: Exception) {}
        }
    }

    companion object {
        private const val REQ = 42
        /** 授权页是否真的打开了（用来发现被系统拦截的情况） */
        @Volatile var opened = false
    }
}
