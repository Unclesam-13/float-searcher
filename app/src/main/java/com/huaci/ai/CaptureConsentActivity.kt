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

    private fun sendResult(resultCode: Int, data: Intent?) {
        val i = Intent(this, FloatService::class.java)
            .setAction(FloatService.ACTION_CAPTURE_RESULT)
            .putExtra(FloatService.EXTRA_RESULT_CODE, resultCode)
        if (data != null) i.putExtra(FloatService.EXTRA_RESULT_DATA, data)
        startForegroundService(i)
        finish()
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }

    companion object { private const val REQ = 42 }
}
