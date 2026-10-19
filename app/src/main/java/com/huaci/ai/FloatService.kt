package com.huaci.ai

import android.animation.ValueAnimator
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.graphics.Typeface
import android.graphics.drawable.Icon
import android.os.Build
import android.os.IBinder
import android.util.Base64
import android.provider.Settings
import android.util.TypedValue
import android.view.ContextThemeWrapper
import android.view.Gravity
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.io.ByteArrayOutputStream
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * 前台服务：用 WindowManager 把「悬浮球」和「回答浮窗」加到系统悬浮层（TYPE_APPLICATION_OVERLAY），
 * 盖在所有 App 上面。
 */
class FloatService : Service() {

    companion object {
        const val ACTION_SHOW = "com.huaci.ai.SHOW"     // 打开浮窗（可带选中文字）
        const val ACTION_BUBBLE = "com.huaci.ai.BUBBLE" // 只显示悬浮球
        const val ACTION_STOP = "com.huaci.ai.STOP"     // 全部关闭
        const val ACTION_CAPTURE_RESULT = "com.huaci.ai.CAPTURE_RESULT" // 截屏授权结果
        const val EXTRA_TEXT = "text"
        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_RESULT_DATA = "resultData"
        private const val CHANNEL = "float"
        private const val NOTIFY_ID = 7

        @Volatile var running = false
            private set

        fun start(ctx: Context, action: String, text: String? = null) {
            val i = Intent(ctx, FloatService::class.java).setAction(action)
            if (text != null) i.putExtra(EXTRA_TEXT, text)
            ctx.startForegroundService(i)
        }
    }

    private lateinit var wm: WindowManager
    private lateinit var ai: AiClient
    private lateinit var ui: Context

    // 悬浮球
    private var bubble: TextView? = null
    private var bubbleLp: WindowManager.LayoutParams? = null
    private var bubbleShown = false

    // 浮窗
    private var panel: PanelRoot? = null
    private var panelLp: WindowManager.LayoutParams? = null
    private var panelShown = false
    private var panelFocusable = false
    private lateinit var quote: TextView
    private lateinit var chips: LinearLayout
    private lateinit var scroll: ScrollView
    private lateinit var thread: LinearLayout
    private lateinit var empty: TextView
    private lateinit var status: TextView
    private lateinit var btnStop: TextView
    private lateinit var input: EditText
    private lateinit var shotBox: View
    private lateinit var shotView: ImageView

    // 截屏
    private lateinit var capture: ScreenCapture
    private var capturing = false
    private var panelWasOpen = false
    private var shotB64: String? = null     // 发给 AI 的 JPEG（base64）
    private var shotThumb: Bitmap? = null   // 浮窗里显示的缩略图
    private var shotW = 0
    private var shotH = 0
    private var shotExpanded = false

    // 对话状态
    private var selected = ""
    private val history = mutableListOf<AiClient.Msg>()
    private var answerView: TextView? = null
    private val answer = StringBuilder()
    private var busy = false
    private var quoteExpanded = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        running = true
        wm = getSystemService(WINDOW_SERVICE) as WindowManager
        ai = AiClient(this)
        ui = ContextThemeWrapper(this, R.style.AppTheme)
        createChannel()
        // 投射结束（闲置释放或在系统里停止）后，把前台类型降回普通
        capture = ScreenCapture(this) { if (running) goForeground() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        goForeground()

        if (!Settings.canDrawOverlays(this)) {
            toast("请先在「划词助手」里开启悬浮窗权限")
            stopSelf()
            return START_NOT_STICKY
        }

        when (intent?.action) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_SHOW -> {
                val text = intent.getStringExtra(EXTRA_TEXT)
                showBubble()
                openPanel(text)
            }
            ACTION_CAPTURE_RESULT -> onCaptureConsent(intent)
            else -> {
                // ACTION_BUBBLE，或被系统杀掉后重启（intent 为 null）
                showBubble()
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        ai.cancel()
        capture.release()
        removeSafely(panel, panelShown); panelShown = false
        removeSafely(bubble, bubbleShown); bubbleShown = false
        super.onDestroy()
    }

    // ================= 前台通知 =================

    private fun createChannel() {
        val ch = NotificationChannel(CHANNEL, "悬浮球", NotificationManager.IMPORTANCE_MIN)
        ch.description = "悬浮球运行时显示，可以从这里打开浮窗或退出"
        ch.setShowBadge(false)
        getSystemService(NotificationManager::class.java).createNotificationChannel(ch)
    }

    private fun goForeground(projection: Boolean = capture.active) {
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        val open = PendingIntent.getForegroundService(
            this, 1, Intent(this, FloatService::class.java).setAction(ACTION_SHOW), flags
        )
        val stop = PendingIntent.getForegroundService(
            this, 2, Intent(this, FloatService::class.java).setAction(ACTION_STOP), flags
        )
        val settings = PendingIntent.getActivity(
            this, 3, Intent(this, MainActivity::class.java), flags
        )
        val icon = Icon.createWithResource(this, R.drawable.ic_stat)
        val n = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("划词助手运行中")
            .setContentText("选中文字 → 问 AI；点这里打开浮窗")
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(icon, "设置", settings).build())
            .addAction(Notification.Action.Builder(icon, "退出", stop).build())
            .build()
        val mp = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
        when {
            Build.VERSION.SDK_INT >= 34 -> startForeground(
                NOTIFY_ID, n,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE or (if (projection) mp else 0)
            )
            Build.VERSION.SDK_INT >= 29 && projection -> startForeground(NOTIFY_ID, n, mp)
            else -> startForeground(NOTIFY_ID, n)
        }
    }

    // ================= 悬浮球 =================

    private fun showBubble() {
        if (bubbleShown) return
        val size = dp(50)
        val b = bubble ?: TextView(ui).apply {
            text = "问"
            gravity = Gravity.CENTER
            setTextColor(getColor(R.color.accent_ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 19f)
            typeface = Typeface.DEFAULT_BOLD
            background = getDrawable(R.drawable.bg_bubble)
            alpha = 0.92f
            contentDescription = "打开划词助手"
        }.also { bubble = it; attachBubbleDrag(it) }

        val lp = bubbleLp ?: overlayParams(size, size, focusable = false, watchOutside = false).apply {
            val (sx, sy) = Prefs.bubblePos(this@FloatService)
            x = if (sx >= 0) sx else screenW() - size - dp(6)
            y = if (sy >= 0) sy else (screenH() * 0.38f).toInt()
        }.also { bubbleLp = it }

        if (addWindow(b, lp)) bubbleShown = true
    }

    private fun hideBubble() {
        removeSafely(bubble, bubbleShown)
        bubbleShown = false
    }

    private fun attachBubbleDrag(v: View) {
        val slop = ViewConfiguration.get(this).scaledTouchSlop
        var downX = 0f; var downY = 0f; var startX = 0; var startY = 0; var dragging = false
        var longFired = false
        // 长按悬浮球 = 截屏
        val longPress = Runnable {
            longFired = true
            v.performHapticFeedback(android.view.HapticFeedbackConstants.LONG_PRESS)
            v.alpha = 0.92f
            startCapture()
        }
        v.setOnTouchListener { _, e ->
            val lp = bubbleLp ?: return@setOnTouchListener false
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = e.rawX; downY = e.rawY; startX = lp.x; startY = lp.y; dragging = false
                    longFired = false
                    v.alpha = 1f
                    v.postDelayed(longPress, ViewConfiguration.getLongPressTimeout().toLong())
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = e.rawX - downX; val dy = e.rawY - downY
                    if (!dragging && (abs(dx) > slop || abs(dy) > slop)) { dragging = true; v.removeCallbacks(longPress) }
                    if (dragging) {
                        lp.x = startX + dx.toInt(); lp.y = startY + dy.toInt()
                        safeUpdate(v, lp)
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    v.removeCallbacks(longPress)
                    v.alpha = 0.92f
                    if (longFired) Unit
                    else if (dragging) snapBubbleToEdge(v, lp)
                    else if (e.actionMasked == MotionEvent.ACTION_UP) { v.performClick(); openPanel(null) }
                }
            }
            true
        }
    }

    private fun snapBubbleToEdge(v: View, lp: WindowManager.LayoutParams) {
        val w = v.width.takeIf { it > 0 } ?: dp(50)
        val targetX = if (lp.x + w / 2 < screenW() / 2) dp(6) else screenW() - w - dp(6)
        lp.y = lp.y.coerceIn(statusBarH(), screenH() - w - dp(24))
        ValueAnimator.ofInt(lp.x, targetX).apply {
            duration = 180
            addUpdateListener { lp.x = it.animatedValue as Int; safeUpdate(v, lp) }
            start()
        }
        Prefs.saveBubblePos(this, targetX, lp.y)
    }

    // ================= 回答浮窗 =================

    private fun openPanel(text: String?) {
        if (panel == null) buildPanel()
        val p = panel ?: return
        val lp = panelLp ?: return

        refreshChips() // 设置里改过预设时立即生效
        if (!text.isNullOrBlank()) setSelected(text.trim())

        if (!panelShown) {
            if (!addWindow(p, lp)) return
            panelShown = true
        }
        hideBubble()

        if (!text.isNullOrBlank() && Prefs.autoRun(this)) {
            Prefs.presets(this).firstOrNull()?.let { runPreset(it) }
        }
    }

    private fun minimizePanel() {
        setPanelFocusable(false)
        removeSafely(panel, panelShown)
        panelShown = false
        showBubble()
    }

    /** 关闭：停止回答、清掉这次的原文和对话，回到悬浮球。「收起」则保留对话。 */
    private fun closePanel() {
        ai.cancel()
        finishAnswer(null, stopped = true)
        setSelected("")
        clearShot()
        clearThread()
        minimizePanel()
    }

    private fun buildPanel() {
        val root = LayoutInflater.from(ui).inflate(R.layout.panel, null) as PanelRoot
        panel = root
        quote = root.findViewById(R.id.quote)
        chips = root.findViewById(R.id.chips)
        scroll = root.findViewById(R.id.scroll)
        thread = root.findViewById(R.id.thread)
        empty = root.findViewById(R.id.empty)
        status = root.findViewById(R.id.status)
        btnStop = root.findViewById(R.id.btnStop)
        input = root.findViewById(R.id.input)
        shotBox = root.findViewById(R.id.shotBox)
        shotView = root.findViewById(R.id.shot)

        panelLp = overlayParams(screenW() - dp(20), panelHeight(false), focusable = false).apply {
            x = dp(10)
            y = statusBarH() + dp(6)
        }

        root.onBack = { if (panelFocusable) setPanelFocusable(false) else minimizePanel() }
        root.onOutsideTouch = { if (panelFocusable) setPanelFocusable(false) }

        root.findViewById<View>(R.id.btnMin).setOnClickListener { minimizePanel() }
        root.findViewById<View>(R.id.btnClose).setOnClickListener { closePanel() }
        root.findViewById<View>(R.id.btnPaste).setOnClickListener { pasteFromClipboard() }
        root.findViewById<View>(R.id.btnShot).setOnClickListener { startCapture() }
        root.findViewById<View>(R.id.shotRemove).setOnClickListener { clearShot(); clearThread() }
        shotView.setOnClickListener {
            shotExpanded = !shotExpanded
            shotView.maxHeight = dp(if (shotExpanded) 320 else 110)
            shotView.requestLayout()
        }
        root.findViewById<View>(R.id.btnCopy).setOnClickListener { copyAnswer() }
        root.findViewById<View>(R.id.btnClear).setOnClickListener { clearThread() }
        root.findViewById<View>(R.id.btnSend).setOnClickListener { sendInput() }
        btnStop.setOnClickListener { ai.cancel(); finishAnswer(null, stopped = true) }

        quote.setOnClickListener {
            quoteExpanded = !quoteExpanded
            quote.maxLines = if (quoteExpanded) 14 else 3
        }

        input.setOnTouchListener { _, e ->
            if (e.actionMasked == MotionEvent.ACTION_DOWN && !panelFocusable) setPanelFocusable(true)
            false
        }

        attachPanelDrag(root.findViewById(R.id.header))
        refreshChips()
        setSelected(selected)
        updateEmpty()
    }

    private fun attachPanelDrag(header: View) {
        var downY = 0f; var downX = 0f; var startY = 0; var startX = 0
        header.setOnTouchListener { _, e ->
            val lp = panelLp ?: return@setOnTouchListener false
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { downX = e.rawX; downY = e.rawY; startX = lp.x; startY = lp.y }
                MotionEvent.ACTION_MOVE -> {
                    lp.x = (startX + (e.rawX - downX).toInt()).coerceIn(-lp.width / 2, screenW() - lp.width / 2)
                    lp.y = (startY + (e.rawY - downY).toInt()).coerceIn(0, screenH() - dp(120))
                    safeUpdate(panel, lp)
                }
            }
            true
        }
    }

    /** 浮窗默认不抢焦点（不影响下面的 App）；点输入框时才拿焦点、弹键盘。 */
    private fun setPanelFocusable(focusable: Boolean) {
        val lp = panelLp ?: return
        if (panelFocusable == focusable) return
        panelFocusable = focusable
        lp.flags = overlayFlags(focusable)
        lp.height = panelHeight(focusable)
        safeUpdate(panel, lp)
        val imm = getSystemService(InputMethodManager::class.java)
        if (focusable) {
            input.requestFocus()
            input.postDelayed({ imm.showSoftInput(input, InputMethodManager.SHOW_IMPLICIT) }, 120)
        } else {
            imm.hideSoftInputFromWindow(input.windowToken, 0)
            input.clearFocus()
        }
    }

    private fun refreshChips() {
        chips.removeAllViews()
        val list = if (shotB64 != null) listOf(Prefs.OCR_PRESET) + Prefs.presets(this) else Prefs.presets(this)
        list.forEach { p ->
            val chip = TextView(ui).apply {
                text = p.name
                setTextColor(getColor(R.color.ink))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
                background = getDrawable(R.drawable.bg_chip)
                gravity = Gravity.CENTER
                setPadding(dp(14), 0, dp(14), 0)
                setOnClickListener { runPreset(p) }
            }
            chips.addView(chip, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(36)
            ).apply { marginEnd = dp(8) })
        }
    }

    private fun setSelected(text: String) {
        val changed = text != selected
        selected = text
        if (text.isNotBlank() && shotB64 != null) clearShot() // 新选了文字，换掉旧截图
        if (panel == null) return
        renderContext()
        if (changed) clearThread()
    }

    /** 浮窗顶部：显示截图或选中的文字。 */
    private fun renderContext() {
        if (panel == null) return
        val thumb = shotThumb
        if (shotB64 != null && thumb != null) {
            shotBox.visibility = View.VISIBLE
            shotView.setImageBitmap(thumb)
            quote.text = "截图 $shotW × $shotH · 点预设或在下面提问"
        } else {
            shotBox.visibility = View.GONE
            shotView.setImageDrawable(null)
            quote.text = if (selected.isBlank())
                "还没有内容。在任意 App 里选中文字 → 菜单里点「问 AI」；或者点右上角「截屏」「粘贴」。"
            else "“$selected”"
        }
    }

    // ================= 截屏 =================

    /** 收起浮窗 → （首次需授权）→ 定格画面 → 选框。 */
    private fun startCapture() {
        if (capturing) return
        capturing = true
        panelWasOpen = panelShown
        if (!capture.active) {
            // 先拉起授权页，再移走悬浮窗（悬浮窗可见时才允许从后台打开页面）
            try {
                startActivity(Intent(this, CaptureConsentActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION))
            } catch (e: Exception) {
                toast("无法打开截屏授权：${e.message}")
                capturing = false
                return
            }
            hideAllWindows()
        } else {
            hideAllWindows()
            grabAndCrop()
        }
    }

    private fun hideAllWindows() {
        if (panel != null) setPanelFocusable(false)
        removeSafely(panel, panelShown); panelShown = false
        hideBubble()
    }

    private fun onCaptureConsent(intent: Intent) {
        val code = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
        @Suppress("DEPRECATION")
        val data: Intent? = intent.getParcelableExtra(EXTRA_RESULT_DATA)
        if (code != android.app.Activity.RESULT_OK || data == null) {
            toast("已取消截屏")
            restoreAfterCapture()
            return
        }
        // Android 14：拿到授权后，先以「屏幕投射」类型进入前台，再建立投射
        try {
            goForeground(projection = true)
        } catch (e: Exception) {
            toast("无法开始截屏：${e.message}")
            restoreAfterCapture()
            return
        }
        if (!capture.start(code, data)) {
            goForeground(projection = false)
            toast("无法开始截屏，请再试一次")
            restoreAfterCapture()
            return
        }
        grabAndCrop()
    }

    private fun grabAndCrop() {
        capture.grab { bmp ->
            if (bmp == null) {
                toast("没截到画面，请再试一次")
                restoreAfterCapture()
                return@grab
            }
            CropOverlay(ui, wm, bmp) { result ->
                if (result == null) restoreAfterCapture()
                else attachShot(result)
            }.show()
        }
    }

    private fun restoreAfterCapture() {
        capturing = false
        if (panelWasOpen) openPanel(null) else showBubble()
    }

    /** 把裁好的截图放进浮窗：压缩成 JPEG，最长边不超过 1568 像素。 */
    private fun attachShot(src: Bitmap) {
        val scale = min(1f, 1568f / max(src.width, src.height))
        val img = if (scale < 1f) Bitmap.createScaledBitmap(
            src, (src.width * scale).roundToInt().coerceAtLeast(1),
            (src.height * scale).roundToInt().coerceAtLeast(1), true
        ) else src
        val out = ByteArrayOutputStream()
        img.compress(Bitmap.CompressFormat.JPEG, 85, out)

        selected = ""
        shotB64 = Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
        shotThumb = img
        shotW = src.width; shotH = src.height
        shotExpanded = false
        capturing = false

        openPanel(null)
        clearThread()
        refreshChips()
        renderContext()
        shotView.maxHeight = dp(110)
    }

    private fun clearShot() {
        shotB64 = null
        shotThumb = null
        shotW = 0; shotH = 0
        if (panel != null) {
            refreshChips()
            renderContext()
        }
    }

    private fun pasteFromClipboard() {
        // Android 10+ 只有拿到焦点的窗口能读剪贴板，所以先让浮窗获得焦点再读
        val wasFocusable = panelFocusable
        if (!wasFocusable) setPanelFocusable(true)
        input.postDelayed({
            val cm = getSystemService(ClipboardManager::class.java)
            val txt = try {
                cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString()
            } catch (_: Exception) { null }
            if (txt.isNullOrBlank()) {
                toast("没读到剪贴板。可以长按下面的输入框手动粘贴")
            } else {
                setSelected(txt.trim())
                if (!wasFocusable) setPanelFocusable(false)
                if (Prefs.autoRun(this)) Prefs.presets(this).firstOrNull()?.let { runPreset(it) }
            }
        }, 200)
    }

    // ================= 提问与回答 =================

    private fun runPreset(p: Preset) {
        val img = shotB64
        if (img != null) {
            clearThread()
            ask(display = "［截图］" + p.name, content = Prefs.buildImagePrompt(p), image = img)
            return
        }
        if (selected.isBlank()) {
            toast("先选中文字，或点右上角「截屏」「粘贴」")
            return
        }
        clearThread()
        ask(display = p.name, content = Prefs.buildPrompt(p, selected))
    }

    private fun sendInput() {
        val q = input.text.toString().trim()
        if (q.isEmpty()) return
        val img = if (history.isEmpty()) shotB64 else null
        val content = when {
            img != null -> "请看这张截图。我的问题：$q"
            history.isEmpty() && selected.isNotBlank() ->
                "关于下面这段文字：\n\"\"\"\n$selected\n\"\"\"\n\n我的问题：$q"
            else -> q
        }
        input.setText("")
        setPanelFocusable(false)
        ask(display = if (img != null) "［截图］$q" else q, content = content, image = img)
    }

    private fun ask(display: String, content: String, image: String? = null) {
        if (busy) { ai.cancel(); finishAnswer(null, stopped = true) }

        addUserBubble(display)
        history.add(AiClient.Msg("user", content, image))
        answer.setLength(0)
        val av = addAnswerView()
        answerView = av
        av.text = "…"
        busy = true
        status.text = "思考中…"
        btnStop.visibility = View.VISIBLE
        updateEmpty()

        ai.stream(
            history.toList(),
            onDelta = { piece ->
                val nearBottom = scroll.scrollY + scroll.height >= thread.height - dp(48)
                answer.append(piece)
                av.text = Md.render(answer.toString())
                status.text = "正在回答…"
                if (nearBottom) scroll.post { scroll.scrollTo(0, thread.height) }
            },
            onDone = { finishAnswer(null, stopped = false) },
            onError = { msg -> finishAnswer(msg, stopped = false) },
            onThinking = { n -> if (answer.isEmpty()) status.text = "深度思考中…（已想 $n 字）" },
        )
    }

    private fun finishAnswer(error: String?, stopped: Boolean) {
        if (!busy) return
        busy = false
        btnStop.visibility = View.GONE
        val av = answerView
        val text = answer.toString()
        if (text.isNotBlank()) {
            history.add(AiClient.Msg("assistant", text))
        } else if (history.lastOrNull()?.role == "user") {
            // 没拿到回答：撤回这条提问，方便重试
            history.removeAt(history.size - 1)
        }
        when {
            error != null -> {
                status.text = "出错了"
                val shown = if (text.isBlank()) "⚠ $error" else "${text}\n\n⚠ $error"
                av?.text = Md.render(shown)
                av?.setTextColor(getColor(if (text.isBlank()) R.color.danger else R.color.ink))
            }
            stopped -> {
                status.text = "已停止"
                if (text.isBlank()) av?.text = "（已停止）"
            }
            else -> status.text = ""
        }
        scroll.post { scroll.scrollTo(0, thread.height) }
    }

    private fun addUserBubble(text: String) {
        val tv = TextView(ui).apply {
            this.text = text
            setTextColor(getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            background = getDrawable(R.drawable.bg_user_msg)
            setPadding(dp(12), dp(7), dp(12), dp(7))
            maxWidth = (screenW() * 0.7f).toInt()
            maxLines = 4
        }
        thread.addView(tv, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { gravity = Gravity.END; topMargin = dp(if (thread.childCount == 0) 0 else 14) })
    }

    private fun addAnswerView(): TextView {
        val tv = TextView(ui).apply {
            setTextColor(getColor(R.color.ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15.5f)
            setLineSpacing(dp(4).toFloat(), 1f)
            setOnLongClickListener {
                copyText(this.text.toString()); true
            }
        }
        thread.addView(tv, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dp(10) })
        return tv
    }

    private fun clearThread() {
        if (busy) { ai.cancel(); busy = false; btnStop.visibility = View.GONE }
        history.clear()
        answer.setLength(0)
        answerView = null
        if (panel != null) {
            thread.removeAllViews()
            status.text = ""
            updateEmpty()
        }
    }

    private fun updateEmpty() {
        val has = thread.childCount > 0
        scroll.visibility = if (has) View.VISIBLE else View.GONE
        empty.visibility = if (has) View.GONE else View.VISIBLE
    }

    private fun copyAnswer() {
        val last = if (answer.isNotBlank()) answer.toString()
                   else history.lastOrNull { it.role == "assistant" }?.content.orEmpty()
        if (last.isBlank()) toast("还没有回答可以复制") else copyText(last)
    }

    private fun copyText(text: String) {
        getSystemService(ClipboardManager::class.java)
            .setPrimaryClip(ClipData.newPlainText("划词助手", text))
        if (Build.VERSION.SDK_INT < 33) toast("已复制")
    }

    // ================= 工具 =================

    private fun overlayFlags(focusable: Boolean, watchOutside: Boolean = true): Int {
        var f = WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED
        if (watchOutside) f = f or WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH
        if (!focusable) f = f or WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
        return f
    }

    private fun overlayParams(w: Int, h: Int, focusable: Boolean, watchOutside: Boolean = true) =
        WindowManager.LayoutParams(
            w, h,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            overlayFlags(focusable, watchOutside),
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            softInputMode = WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE
        }

    /** 打字时浮窗变矮，给键盘让出下半屏。 */
    private fun panelHeight(typing: Boolean) =
        (screenH() * if (typing) 0.46f else 0.62f).toInt()

    /** 悬浮窗权限在运行中被关掉时，addView 会抛异常：提示并退出，而不是崩溃。 */
    private fun addWindow(v: View, lp: WindowManager.LayoutParams): Boolean {
        if (!Settings.canDrawOverlays(this)) {
            toast("悬浮窗权限被关闭了，请在「划词助手」里重新开启")
            stopSelf()
            return false
        }
        return try {
            wm.addView(v, lp); true
        } catch (e: RuntimeException) {
            toast("无法显示悬浮窗：${e.message ?: "权限不足"}")
            stopSelf()
            false
        }
    }

    private fun safeUpdate(v: View?, lp: WindowManager.LayoutParams) {
        if (v == null || !v.isAttachedToWindow) return
        try { wm.updateViewLayout(v, lp) } catch (_: Exception) {}
    }

    private fun removeSafely(v: View?, shown: Boolean) {
        if (v == null || !shown) return
        try { wm.removeView(v) } catch (_: Exception) {}
    }

    private fun screenW() = resources.displayMetrics.widthPixels
    private fun screenH() = resources.displayMetrics.heightPixels

    private fun statusBarH(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dp(24)
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density + 0.5f).toInt()

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
}
