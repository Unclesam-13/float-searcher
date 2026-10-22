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
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.Rect
import android.graphics.PixelFormat
import android.graphics.Typeface
import android.graphics.drawable.Icon
import android.os.Build
import android.os.IBinder
import android.util.Base64
import android.provider.Settings
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.TextPaint
import android.text.method.LinkMovementMethod
import android.text.style.ClickableSpan
import android.text.style.ForegroundColorSpan
import android.text.style.StyleSpan
import android.net.Uri
import android.util.TypedValue
import android.view.ContextThemeWrapper
import android.view.Gravity
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.view.KeyEvent
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.text.Text
import com.google.mlkit.vision.text.TextRecognition
import com.google.mlkit.vision.text.chinese.ChineseTextRecognizerOptions
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

        /** 当前运行的服务（给「复制后提示」的无障碍服务用；同一进程、主线程） */
        @Volatile var instance: FloatService? = null
            private set

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
    private lateinit var body: LinearLayout
    private lateinit var content: View
    private lateinit var resizeGrip: View
    private lateinit var scanLayer: View      // 取景框上的按钮和四角手柄
    private lateinit var scanOk: View
    private lateinit var scanCancel: View
    private var savedPanelGeo: IntArray? = null // 进取景框前浮窗的 x, y, w, h

    // 中译英速查
    private val quickAi by lazy { AiClient(this) }
    private var quick: PanelRoot? = null
    private var quickLp: WindowManager.LayoutParams? = null
    private var quickShown = false
    private lateinit var quickInput: EditText
    private lateinit var quickResult: TextView
    private lateinit var quickScroll: MaxHeightScrollView
    private lateinit var quickBusy: View
    private lateinit var quickHint: TextView
    private var quickAnswer = ""
    private var quickAsked = ""
    private var quickBusyNow = false
    private val quickCache = object : LinkedHashMap<String, String>(32, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, String>?) = size > 60
    }

    // 剪贴板检测
    private val mainHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private lateinit var clipCard: View
    private lateinit var clipText: TextView
    private var pendingClip: String? = null
    private var clipChip: View? = null
    private val hideChipLater = Runnable { hideClipChip() }
    private var typingRestoreH = 0
    private var typingRestoreY = 0

    // 放大镜识字
    private var scanMode = false
    private var scanRect: Rect? = null
    private val recognizer by lazy {
        TextRecognition.getClient(ChineseTextRecognizerOptions.Builder().build())
    }
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
    private var cropOverlay: CropOverlay? = null
    private var consentToken = 0
    private var hiddenAt = 0L   // 悬浮窗移走的时刻：只用这之后的屏幕画面

    // 对话状态
    private var selected = ""
    private val history = mutableListOf<AiClient.Msg>()
    private var answerView: TextView? = null
    private var pendingSources: List<AiClient.SearchHit>? = null
    private lateinit var btnWeb: TextView
    private val answer = StringBuilder()
    private var busy = false
    private var quoteExpanded = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        running = true
        instance = this
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

    /** 横竖屏切换：浮窗换成该方向记住的大小，悬浮球留在屏幕内 */
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        val p = panel
        val lp = panelLp
        if (p != null && lp != null) {
            if (scanMode) exitScanMode()
            if (panelFocusable) setPanelFocusable(false)
            applySavedPanelGeometry(lp, resetPosition = true)
            safeUpdate(p, lp)
        }
        val q = quick
        val qlp = quickLp
        if (q != null && qlp != null && quickShown) {
            placeQuick(qlp)
            safeUpdate(q, qlp)
        }
        val b = bubble
        val blp = bubbleLp
        if (b != null && blp != null) {
            val size = dp(50)
            blp.x = if (blp.x + size / 2 < screenW() / 2) dp(6) else screenW() - size - dp(6)
            blp.y = blp.y.coerceIn(0, (screenH() - size).coerceAtLeast(0))
            safeUpdate(b, blp)
        }
    }

    override fun onDestroy() {
        running = false
        instance = null
        hideClipChip()
        ai.cancel()
        consentToken++
        cropOverlay?.dismiss(); cropOverlay = null
        capture.release()
        try { recognizer.close() } catch (_: Exception) {}
        removeSafely(panel, panelShown); panelShown = false
        if (quick != null) quickAi.cancel()
        removeSafely(quick, quickShown); quickShown = false
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

    /** 返回 false 表示没能带上「屏幕投射」类型（授权已失效），已退回普通前台。 */
    private fun goForeground(projection: Boolean = capture.active): Boolean {
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
        try {
            when {
                Build.VERSION.SDK_INT >= 34 -> startForeground(
                    NOTIFY_ID, n,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE or (if (projection) mp else 0)
                )
                Build.VERSION.SDK_INT >= 29 && projection -> startForeground(NOTIFY_ID, n, mp)
                else -> startForeground(NOTIFY_ID, n)
            }
        } catch (e: SecurityException) {
            // 截屏授权已失效时不能再带投射类型，退回普通前台
            if (!projection) throw e
            if (Build.VERSION.SDK_INT >= 34)
                startForeground(NOTIFY_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            else startForeground(NOTIFY_ID, n)
            return false
        }
        return true
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
                    else if (e.actionMasked == MotionEvent.ACTION_UP) { v.performClick(); onBubbleTap() }
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
        updateWebBtn()
        if (!text.isNullOrBlank()) setSelected(text.trim())

        if (!panelShown) {
            if (!addWindow(p, lp)) return
            panelShown = true
        }
        hideBubble()

        if (!text.isNullOrBlank() && Prefs.autoRun(this)) {
            Prefs.presets(this).firstOrNull()?.let { runPreset(it) }
        }
        hideClipChip()
        // 从悬浮球/通知打开时，看看剪贴板里有没有新复制的内容
        if (text.isNullOrBlank() && Prefs.clipCheck(this)) {
            mainHandler.postDelayed({ checkClipboard(fromCopy = false) }, 150)
        }
    }

    // ================= 中译英速查 =================

    /** 点悬浮球：按设置里选的模式打开 */
    private fun onBubbleTap() {
        if (Prefs.mode(this) == Prefs.MODE_QUICK) openQuick() else openPanel(null)
    }

    private fun buildQuick() {
        val root = LayoutInflater.from(ui).inflate(R.layout.quick, null) as PanelRoot
        quick = root
        quickInput = root.findViewById(R.id.quickInput)
        quickResult = root.findViewById(R.id.quickResult)
        quickScroll = root.findViewById(R.id.quickScroll)
        quickBusy = root.findViewById(R.id.quickBusy)
        quickHint = root.findViewById(R.id.quickHint)

        // 单行输入（回车 = 翻译），但长句子可以折行显示
        quickInput.setHorizontallyScrolling(false)
        quickInput.maxLines = 4
        quickInput.setOnEditorActionListener { _, _, event ->
            // 输入法的「搜索/完成」键：event 为空；物理回车：只认按下那一次
            if (event == null ||
                (event.keyCode == KeyEvent.KEYCODE_ENTER && event.action == KeyEvent.ACTION_DOWN)) {
                quickTranslate()
            }
            true
        }
        quickResult.setOnClickListener {
            if (quickBusyNow || quickAnswer.isBlank()) return@setOnClickListener
            // 只复制主译法（不带「也可：」那行）
            val main = quickAnswer.lines()
                .takeWhile { !it.trimStart().startsWith("也可") }
                .joinToString("\n").trim().ifBlank { quickAnswer.trim() }
            copyText(main)
            quickHint.text = "已复制"
            quickHint.postDelayed({ if (!quickBusyNow && quickAnswer.isNotBlank()) quickHint.text = "点译文复制" }, 1500)
        }
        root.onBack = { closeQuick() }
        root.onOutsideTouch = { closeQuick() }
    }

    private fun quickParams() = WindowManager.LayoutParams(
        dp(300), WindowManager.LayoutParams.WRAP_CONTENT,
        WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
        WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
            WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or
            WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
        PixelFormat.TRANSLUCENT
    ).apply {
        gravity = Gravity.TOP or Gravity.START
        // 一拿到焦点就弹出键盘
        softInputMode = WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE or
            WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING
    }

    /** 放在屏幕上方，避开键盘；横屏时窄一点 */
    private fun placeQuick(lp: WindowManager.LayoutParams) {
        val land = isLandscape()
        lp.width = if (land) min(dp(520), (screenW() * 0.6f).toInt()) else screenW() - dp(20)
        lp.x = (screenW() - lp.width) / 2
        lp.y = if (land) dp(8) else statusBarH() + dp(8)
        quickScroll.maxHeightPx = (screenH() * if (land) 0.2f else 0.3f).toInt().coerceAtLeast(dp(60))
    }

    private fun openQuick() {
        if (capturing) return
        if (quick == null) buildQuick()
        val q = quick ?: return
        val lp = quickLp ?: quickParams().also { quickLp = it }
        placeQuick(lp)
        lp.alpha = Prefs.panelAlpha(this)
        if (!quickShown) {
            if (!addWindow(q, lp)) return
            quickShown = true
        } else safeUpdate(q, lp)
        hideBubble()
        hideClipChip()
        showQuickKeyboard()
    }

    private fun showQuickKeyboard() {
        val imm = getSystemService(InputMethodManager::class.java)
        val show = Runnable {
            if (!quickShown) return@Runnable
            quickInput.requestFocus()
            imm.showSoftInput(quickInput, 0)
        }
        // 窗口拿到焦点有时慢一点：多试一次
        quickInput.postDelayed(show, 80)
        quickInput.postDelayed(show, 320)
    }

    /** 关掉速查框，回到悬浮球；下次打开是空的输入框 */
    private fun closeQuick() {
        if (!quickShown) return
        getSystemService(InputMethodManager::class.java).hideSoftInputFromWindow(quickInput.windowToken, 0)
        quickAi.cancel()
        setQuickBusy(false)
        quickInput.setText("")
        quickAnswer = ""; quickAsked = ""
        quickScroll.visibility = View.GONE
        quickHint.visibility = View.GONE
        removeSafely(quick, quickShown)
        quickShown = false
        showBubble()
    }

    private fun setQuickBusy(on: Boolean) {
        quickBusyNow = on
        if (quick != null) quickBusy.visibility = if (on) View.VISIBLE else View.GONE
    }

    private fun quickTranslate() {
        val text = quickInput.text.toString().trim()
        if (text.isEmpty()) return
        if (quickBusyNow && text == quickAsked) return
        quickAsked = text

        quickCache[text]?.let { cached ->
            quickAi.cancel(); setQuickBusy(false)
            showQuickResult(cached, done = true)
            quickInput.selectAll()
            return
        }

        quickAnswer = ""
        setQuickBusy(true)
        quickScroll.visibility = View.VISIBLE
        quickResult.setTextColor(getColor(R.color.ink_soft))
        quickResult.text = "翻译中…"
        quickHint.visibility = View.GONE
        val sb = StringBuilder()
        quickAi.stream(
            listOf(AiClient.Msg("user", text)),
            onDelta = { piece ->
                sb.append(piece)
                showQuickResult(sb.toString(), done = false)
            },
            onDone = {
                setQuickBusy(false)
                val r = cleanTranslation(sb.toString())
                if (r.isBlank()) {
                    quickResult.setTextColor(getColor(R.color.danger))
                    quickResult.text = "没有拿到译文，再按一次回车试试"
                } else {
                    quickCache[text] = r
                    showQuickResult(r, done = true)
                }
                // 选中原文：直接打字就会替换，接着查下一个
                quickInput.selectAll()
            },
            onError = { msg ->
                setQuickBusy(false)
                quickAnswer = ""
                quickResult.setTextColor(getColor(R.color.danger))
                quickResult.text = msg
                quickHint.visibility = View.GONE
            },
            system = AiClient.TRANSLATE_PROMPT,
            noThinking = true,
        )
    }

    private fun showQuickResult(t: String, done: Boolean) {
        val shown = cleanTranslation(t)
        quickAnswer = shown
        quickScroll.visibility = View.VISIBLE
        quickResult.setTextColor(getColor(R.color.ink))
        quickResult.text = shown.ifBlank { "翻译中…" }
        if (done) {
            quickHint.text = "点译文复制"
            quickHint.visibility = View.VISIBLE
        }
    }

    /** 去掉模型偶尔加的引号、前缀 */
    private fun cleanTranslation(t: String): String {
        var s = t.trim()
        for (p in listOf("译文：", "译文:", "Translation:", "英文：", "英文:")) {
            if (s.startsWith(p, ignoreCase = true)) s = s.substring(p.length).trim()
        }
        val pairs = listOf('"' to '"', '“' to '”', '「' to '」', '\'' to '\'')
        for ((a, b) in pairs) {
            if (s.length >= 2 && s.first() == a && s.last() == b && s.count { it == a || it == b } <= 2) {
                s = s.substring(1, s.length - 1).trim()
            }
        }
        return s.replace("**", "")
    }

    // ================= 剪贴板检测 =================

    /** 无障碍服务发现用户刚点了「复制」 */
    fun onCopyDetected() {
        if (!running || capturing || scanMode) return
        // 等对方 App 把内容写进剪贴板
        mainHandler.postDelayed({ checkClipboard(fromCopy = true) }, 300)
    }

    private fun checkClipboard(fromCopy: Boolean) {
        if (!running || capturing || scanMode) return
        if (panelFocusable) handleClip(ClipReader.readNow(this), fromCopy)
        else ClipReader.read(this, wm) { handleClip(it, fromCopy) }
    }

    private fun handleClip(text: String?, fromCopy: Boolean) {
        if (!running || text.isNullOrBlank() || quickShown) return
        if (text.hashCode() == Prefs.lastClip(this)) return // 提示过了
        if (text == selected.trim()) { markClipSeen(text); return }
        if (panelShown) showClipCard(text)
        else if (fromCopy) showClipChip(text)
    }

    private fun markClipSeen(text: String?) {
        if (!text.isNullOrBlank()) Prefs.setLastClip(this, text.trim().hashCode())
    }

    private fun showClipCard(text: String) {
        if (panel == null) return
        pendingClip = text
        clipText.text = "剪贴板：" + text.replace('\n', ' ')
        clipCard.visibility = View.VISIBLE
        scroll.post { scroll.scrollTo(0, 0) }
    }

    private fun hideClipCard() {
        if (panel != null) clipCard.visibility = View.GONE
    }

    private fun useClip() {
        val t = pendingClip ?: return
        pendingClip = null
        markClipSeen(t)
        hideClipCard()
        hideClipChip()
        openPanel(t)
    }

    private fun dismissClip() {
        markClipSeen(pendingClip)
        pendingClip = null
        hideClipCard()
        hideClipChip()
    }

    /** 浮窗收着时：在悬浮球旁边弹一个「问 AI：…」小提示，6 秒后消失 */
    private fun showClipChip(text: String) {
        hideClipChip()
        pendingClip = text
        val preview = text.replace('\n', ' ').let { if (it.length > 14) it.take(14) + "…" else it }
        val chip = TextView(ui).apply {
            this.text = "问 AI：$preview"
            setTextColor(getColor(R.color.accent_ink))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            background = getDrawable(R.drawable.bg_btn_accent)
            setPadding(dp(14), dp(9), dp(14), dp(9))
            maxLines = 1
            setOnClickListener { useClip() }
        }
        val blp = bubbleLp
        val lp = overlayParams(
            WindowManager.LayoutParams.WRAP_CONTENT, WindowManager.LayoutParams.WRAP_CONTENT,
            focusable = false, watchOutside = false
        ).apply {
            if (blp != null && bubbleShown) {
                y = blp.y + dp(7)
                if (blp.x > screenW() / 2) {
                    gravity = Gravity.TOP or Gravity.END
                    x = screenW() - blp.x + dp(6)
                } else {
                    x = blp.x + dp(56)
                }
            } else {
                gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
                x = 0
                y = statusBarH() + dp(12)
            }
        }
        try {
            wm.addView(chip, lp)
            clipChip = chip
            mainHandler.postDelayed(hideChipLater, 6000)
        } catch (_: Exception) {}
    }

    private fun hideClipChip() {
        mainHandler.removeCallbacks(hideChipLater)
        val c = clipChip ?: return
        clipChip = null
        try { wm.removeView(c) } catch (_: Exception) {}
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
        body = root.findViewById(R.id.body)
        content = root.getChildAt(0)
        resizeGrip = root.findViewById(R.id.resize)
        buildScanLayer(root)

        panelLp = overlayParams(dp(300), dp(300), focusable = false).also {
            applySavedPanelGeometry(it, resetPosition = true)
            it.alpha = Prefs.panelAlpha(this)
        }

        // 调节：透明度 + 默认大小
        val adjustRow = root.findViewById<View>(R.id.adjustRow)
        val alphaSeek = root.findViewById<SeekBar>(R.id.alphaSeek)
        val alphaLabel = root.findViewById<TextView>(R.id.alphaLabel)
        fun showAlpha(a: Float) { alphaLabel.text = "透明度 ${((1f - a) * 100).roundToInt()}%" }
        val a0 = Prefs.panelAlpha(this)
        alphaSeek.progress = ((1f - a0) * 100).roundToInt()
        showAlpha(a0)
        alphaSeek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, progress: Int, fromUser: Boolean) {
                val a = 1f - progress / 100f
                showAlpha(a)
                val lp = panelLp ?: return
                lp.alpha = a
                safeUpdate(panel, lp)
            }
            override fun onStartTrackingTouch(sb: SeekBar) {}
            override fun onStopTrackingTouch(sb: SeekBar) {
                Prefs.savePanelAlpha(this@FloatService, 1f - sb.progress / 100f)
            }
        })
        root.findViewById<View>(R.id.btnAdjust).setOnClickListener {
            adjustRow.visibility = if (adjustRow.visibility == View.VISIBLE) View.GONE else View.VISIBLE
        }
        root.findViewById<View>(R.id.btnResetSize).setOnClickListener {
            val lp = panelLp ?: return@setOnClickListener
            if (panelFocusable) setPanelFocusable(false)
            Prefs.savePanelSize(this, isLandscape(), 0, 0)
            applySavedPanelGeometry(lp, resetPosition = true)
            safeUpdate(panel, lp)
        }
        attachResize(resizeGrip)

        root.onBack = { if (panelFocusable) setPanelFocusable(false) else minimizePanel() }
        root.onOutsideTouch = { if (panelFocusable) setPanelFocusable(false) }

        root.findViewById<View>(R.id.btnMin).setOnClickListener { minimizePanel() }
        root.findViewById<View>(R.id.btnClose).setOnClickListener { closePanel() }
        root.findViewById<View>(R.id.btnPaste).setOnClickListener { pasteFromClipboard() }
        root.findViewById<View>(R.id.btnShot).setOnClickListener { startCapture() }
        root.findViewById<View>(R.id.btnScan).setOnClickListener { enterScanMode() }
        clipCard = root.findViewById(R.id.clipCard)
        clipText = root.findViewById(R.id.clipText)
        root.findViewById<View>(R.id.clipUse).setOnClickListener { useClip() }
        root.findViewById<View>(R.id.clipDismiss).setOnClickListener { dismissClip() }
        root.findViewById<View>(R.id.shotRemove).setOnClickListener { clearShot(); clearThread() }
        shotView.setOnClickListener {
            shotExpanded = !shotExpanded
            shotView.maxHeight = dp(if (shotExpanded) 320 else 110)
            shotView.requestLayout()
        }
        root.findViewById<View>(R.id.btnCopy).setOnClickListener { copyAnswer() }
        btnWeb = root.findViewById(R.id.btnWeb)
        btnWeb.setOnClickListener {
            Prefs.setWebSearch(this, !Prefs.webSearch(this))
            updateWebBtn()
            toast(if (Prefs.webSearch(this)) "已开启联网：需要时会先搜索，回答附来源" else "已关闭联网")
        }
        updateWebBtn()
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

    /**
     * 按住标题栏拖动 = 移动浮窗。
     * 长按标题栏（不动约半秒）后再拖 = 放大镜：浮窗变成取景框，松手识别框里的文字。
     */
    private fun attachPanelDrag(header: View) {
        val slop = ViewConfiguration.get(this).scaledTouchSlop
        var downY = 0f; var downX = 0f; var startY = 0; var startX = 0; var moved = false
        val longPress = Runnable {
            if (!moved) {
                header.performHapticFeedback(android.view.HapticFeedbackConstants.LONG_PRESS)
                enterScanMode(fromLongPress = true, fingerX = downX, fingerY = downY)
            }
        }
        header.setOnTouchListener { _, e ->
            val lp = panelLp ?: return@setOnTouchListener false
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = e.rawX; downY = e.rawY; startX = lp.x; startY = lp.y; moved = false
                    header.postDelayed(longPress, 450)
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = e.rawX - downX; val dy = e.rawY - downY
                    if (!moved && (abs(dx) > slop || abs(dy) > slop)) {
                        moved = true
                        header.removeCallbacks(longPress)
                    }
                    if (moved || scanMode) {
                        lp.x = (startX + dx.toInt()).coerceIn(-lp.width / 2, screenW() - lp.width / 2)
                        lp.y = (startY + dy.toInt()).coerceIn(0, (screenH() - dp(60)).coerceAtLeast(0))
                        safeUpdate(panel, lp)
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    // 进入放大镜后，后续手势由整块浮窗接管（见 enterScanMode）
                    header.removeCallbacks(longPress)
                }
            }
            true
        }
    }

    /** 右下角拖动改大小，松手后记住（横竖屏分开记） */
    private fun attachResize(grip: View) {
        var downX = 0f; var downY = 0f; var startW = 0; var startH = 0
        grip.setOnTouchListener { _, e ->
            val lp = panelLp ?: return@setOnTouchListener false
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    if (panelFocusable) setPanelFocusable(false)
                    downX = e.rawX; downY = e.rawY; startW = lp.width; startH = lp.height
                }
                MotionEvent.ACTION_MOVE -> {
                    lp.width = (startW + (e.rawX - downX).toInt())
                        .coerceIn(minPanelW(), (screenW() - lp.x).coerceAtLeast(minPanelW()))
                    lp.height = (startH + (e.rawY - downY).toInt())
                        .coerceIn(minPanelH(), (screenH() - lp.y).coerceAtLeast(minPanelH()))
                    safeUpdate(panel, lp)
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL ->
                    Prefs.savePanelSize(this, isLandscape(), lp.width, lp.height)
            }
            true
        }
    }

    // ---------- 浮窗尺寸 ----------

    private fun isLandscape() = screenW() > screenH()
    private fun minPanelW() = min(dp(240), screenW())
    private fun minPanelH() = min(dp(190), screenH())

    private fun defaultPanelSize(): Pair<Int, Int> =
        if (isLandscape()) (screenW() * 0.48f).toInt().coerceAtLeast(dp(300)) to
            (screenH() - statusBarH() - dp(16))
        else (screenW() - dp(20)) to (screenH() * 0.55f).toInt()

    /** 用记住的（或默认）大小，并保证整个浮窗都在屏幕里 */
    private fun applySavedPanelGeometry(lp: WindowManager.LayoutParams, resetPosition: Boolean) {
        val (sw, sh) = Prefs.panelSize(this, isLandscape())
        val (dw, dh) = defaultPanelSize()
        lp.width = (if (sw > 0) sw else dw).coerceIn(minPanelW(), screenW())
        lp.height = (if (sh > 0) sh else dh).coerceIn(minPanelH(), screenH())
        if (resetPosition) {
            if (isLandscape()) { lp.x = screenW() - lp.width - dp(10); lp.y = dp(8) }
            else { lp.x = dp(10); lp.y = statusBarH() + dp(6) }
        }
        lp.x = lp.x.coerceIn(0, (screenW() - lp.width).coerceAtLeast(0))
        lp.y = lp.y.coerceIn(0, (screenH() - lp.height).coerceAtLeast(0))
    }

    // ---------- 放大镜识字 ----------

    /** 取景框上的「✕」「识别」按钮和四角手柄（只在识字时显示） */
    private fun buildScanLayer(root: PanelRoot) {
        val layer = android.widget.FrameLayout(ui).apply { visibility = View.GONE }
        val row = LinearLayout(ui).apply { orientation = LinearLayout.HORIZONTAL }
        fun pill(label: String, accent: Boolean) = TextView(ui).apply {
            text = label
            gravity = Gravity.CENTER
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setTextColor(getColor(if (accent) R.color.accent_ink else R.color.ink))
            background = getDrawable(if (accent) R.drawable.bg_btn_accent else R.drawable.bg_chip)
            setPadding(dp(14), 0, dp(14), 0)
        }
        val cancel = pill("✕", false)
        val ok = pill("识别", true).apply { typeface = Typeface.DEFAULT_BOLD }
        row.addView(cancel, LinearLayout.LayoutParams(dp(40), dp(32)).apply { marginEnd = dp(8) })
        row.addView(ok, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, dp(32)))
        layer.addView(row, android.widget.FrameLayout.LayoutParams(
            android.widget.FrameLayout.LayoutParams.WRAP_CONTENT,
            android.widget.FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.CENTER_HORIZONTAL
        ).apply { topMargin = dp(6) })
        // 四角手柄：提示可以拖角改大小
        for (g in intArrayOf(Gravity.TOP or Gravity.START, Gravity.TOP or Gravity.END,
                             Gravity.BOTTOM or Gravity.START, Gravity.BOTTOM or Gravity.END)) {
            val dot = View(ui).apply { background = getDrawable(R.drawable.bg_scan_handle) }
            layer.addView(dot, android.widget.FrameLayout.LayoutParams(dp(14), dp(14), g))
        }
        root.addView(layer, android.widget.FrameLayout.LayoutParams(
            android.widget.FrameLayout.LayoutParams.MATCH_PARENT,
            android.widget.FrameLayout.LayoutParams.MATCH_PARENT
        ))
        scanLayer = layer; scanOk = ok; scanCancel = cancel
    }

    /**
     * 进入识字取景框。
     * @param fromLongPress 由长按标题栏进入：这一下拖动松手就直接识别
     * @param fingerX/fingerY 长按时手指的屏幕位置（取景框放到手指下面）
     */
    private fun enterScanMode(fromLongPress: Boolean = false, fingerX: Float = 0f, fingerY: Float = 0f) {
        val p = panel ?: return
        val lp = panelLp ?: return
        if (panelFocusable) setPanelFocusable(false)
        if (scanMode) return
        scanMode = true
        savedPanelGeo = intArrayOf(lp.x, lp.y, lp.width, lp.height)

        // 取景框用自己记住的大小
        val (sw, sh) = Prefs.scanSize(this)
        lp.width = (if (sw > 0) sw else min((screenW() * 0.8f).toInt(), dp(340))).coerceIn(minScanW(), screenW())
        lp.height = (if (sh > 0) sh else dp(130)).coerceIn(minScanH(), screenH())
        if (fromLongPress) { lp.x = (fingerX - lp.width / 2f).toInt(); lp.y = (fingerY - dp(20)).toInt() }
        lp.x = lp.x.coerceIn(0, (screenW() - lp.width).coerceAtLeast(0))
        lp.y = lp.y.coerceIn(0, (screenH() - lp.height).coerceAtLeast(0))

        // 整块取景框接管触摸：拖内部移动，拖边/角改大小，点按钮识别或取消
        val edge = dp(30).toFloat()
        var tracking = false
        var quickRelease = fromLongPress
        var ax = 0f; var ay = 0f; var sx = 0; var sy = 0; var swd = 0; var sht = 0
        var eL = false; var eR = false; var eT = false; var eB = false
        var moved = false; var resized = false
        p.scanTouch = { e ->
            if (!tracking) {
                tracking = true; moved = false; resized = false
                ax = e.rawX; ay = e.rawY; sx = lp.x; sy = lp.y; swd = lp.width; sht = lp.height
                // 由长按进入的那一下只移动；之后按在边缘附近就是改大小
                if (quickRelease) { eL = false; eR = false; eT = false; eB = false }
                else {
                    eL = e.x < edge; eR = e.x > p.width - edge
                    eT = e.y < edge; eB = e.y > p.height - edge
                }
            }
            when (e.actionMasked) {
                MotionEvent.ACTION_MOVE -> {
                    val dx = (e.rawX - ax).toInt(); val dy = (e.rawY - ay).toInt()
                    if (abs(dx) > 6 || abs(dy) > 6) moved = true
                    if (moved) {
                        if (eL || eR || eT || eB) {
                            resized = true
                            if (eL) { val nx = (sx + dx).coerceIn(0, sx + swd - minScanW()); lp.width = swd + (sx - nx); lp.x = nx }
                            if (eR) lp.width = (swd + dx).coerceIn(minScanW(), screenW() - lp.x)
                            if (eT) { val ny = (sy + dy).coerceIn(0, sy + sht - minScanH()); lp.height = sht + (sy - ny); lp.y = ny }
                            if (eB) lp.height = (sht + dy).coerceIn(minScanH(), screenH() - lp.y)
                        } else {
                            lp.x = (sx + dx).coerceIn(0, (screenW() - lp.width).coerceAtLeast(0))
                            lp.y = (sy + dy).coerceIn(0, (screenH() - lp.height).coerceAtLeast(0))
                        }
                        safeUpdate(p, lp)
                    }
                }
                MotionEvent.ACTION_UP -> {
                    tracking = false
                    when {
                        quickRelease -> { quickRelease = false; startScan() }
                        !moved && hit(scanOk, e) -> startScan()
                        !moved && hit(scanCancel, e) -> exitScanMode()
                        resized -> Prefs.saveScanSize(this, lp.width, lp.height)
                    }
                }
                MotionEvent.ACTION_CANCEL -> { tracking = false; quickRelease = false }
            }
        }
        // 用透明而不是隐藏：隐藏会打断正在拖动的手指
        content.alpha = 0f
        resizeGrip.alpha = 0f
        scanLayer.visibility = View.VISIBLE
        p.background = getDrawable(R.drawable.bg_scan_frame)
        lp.alpha = 1f
        safeUpdate(p, lp)
        if (!fromLongPress) toast("拖框移动，拖边或角改大小，点「识别」")
    }

    private fun minScanW() = min(dp(120), screenW())
    private fun minScanH() = min(dp(64), screenH())

    /** 手指抬起的位置是否落在某个按钮上 */
    private fun hit(v: View, e: MotionEvent): Boolean {
        val loc = IntArray(2)
        v.getLocationOnScreen(loc)
        val pad = dp(6)
        return e.rawX >= loc[0] - pad && e.rawX <= loc[0] + v.width + pad &&
               e.rawY >= loc[1] - pad && e.rawY <= loc[1] + v.height + pad
    }

    private fun exitScanMode() {
        if (!scanMode) return
        scanMode = false
        val p = panel ?: return
        p.scanTouch = null
        content.alpha = 1f
        resizeGrip.alpha = 1f
        scanLayer.visibility = View.GONE
        p.background = getDrawable(R.drawable.bg_panel)
        val lp = panelLp ?: return
        savedPanelGeo?.let { g -> lp.x = g[0]; lp.y = g[1]; lp.width = g[2]; lp.height = g[3] }
        savedPanelGeo = null
        lp.alpha = Prefs.panelAlpha(this)
        safeUpdate(p, lp)
    }

    /** 记下浮窗在屏幕上的位置，移走浮窗后截屏，只识别这块区域的文字 */
    private fun startScan() {
        val p = panel ?: return exitScanMode()
        val loc = IntArray(2)
        p.getLocationOnScreen(loc)
        val r = Rect(loc[0], loc[1], loc[0] + p.width, loc[1] + p.height)
        exitScanMode()
        startCapture(scan = r)
    }

    private fun recognizeText(shot: Bitmap, r: Rect) {
        val l = r.left.coerceIn(0, shot.width - 1)
        val t = r.top.coerceIn(0, shot.height - 1)
        val rr = r.right.coerceIn(l + 1, shot.width)
        val bb = r.bottom.coerceIn(t + 1, shot.height)
        val part = try { Bitmap.createBitmap(shot, l, t, rr - l, bb - t) } catch (_: Exception) { null }
        if (part == null) {
            toast("识别失败，请再试一次")
            restoreAfterCapture()
            return
        }
        recognizer.process(InputImage.fromBitmap(part, 0))
            .addOnSuccessListener { result ->
                if (!running) return@addOnSuccessListener
                val text = joinRecognized(result)
                if (text.isBlank()) {
                    toast("框里没识别到文字，把浮窗挪到文字上再试")
                    restoreAfterCapture()
                } else {
                    capturing = false
                    openPanel(text) // 和划词一样：放进原文，可点预设或追问
                }
            }
            .addOnFailureListener {
                if (!running) return@addOnFailureListener
                toast("识别失败：${it.message ?: "未知错误"}")
                restoreAfterCapture()
            }
    }

    /** 中文行与行之间直接相连，英文行之间加空格；段落之间换行 */
    private fun joinRecognized(t: Text): String =
        t.textBlocks.joinToString("\n") { block ->
            val sb = StringBuilder()
            block.lines.forEach { line ->
                val s = line.text.trim()
                if (s.isEmpty()) return@forEach
                if (sb.isNotEmpty()) {
                    val prev = sb[sb.length - 1]
                    if (!(isCjk(prev) || isCjk(s[0]))) sb.append(' ')
                }
                sb.append(s)
            }
            sb.toString()
        }.trim()

    private fun isCjk(c: Char) = Character.UnicodeScript.of(c.code) == Character.UnicodeScript.HAN ||
        c in '\u3000'..'\u303F' || c in '\uFF00'..'\uFFEF'

    /** 浮窗默认不抢焦点（不影响下面的 App）；点输入框时才拿焦点、弹键盘。 */
    private fun setPanelFocusable(focusable: Boolean) {
        val lp = panelLp ?: return
        if (panelFocusable == focusable) return
        panelFocusable = focusable
        lp.flags = overlayFlags(focusable)
        if (focusable) {
            // 打字时（竖屏）让出下半屏给键盘
            typingRestoreH = lp.height; typingRestoreY = lp.y
            if (!isLandscape()) {
                lp.height = min(lp.height, (screenH() * 0.46f).toInt()).coerceAtLeast(minPanelH())
                lp.y = min(lp.y, statusBarH() + dp(6))
            }
        } else if (typingRestoreH > 0) {
            lp.height = typingRestoreH; lp.y = typingRestoreY
            typingRestoreH = 0
        }
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
    private fun startCapture(scan: Rect? = null) {
        if (capturing) return
        capturing = true
        scanRect = scan
        panelWasOpen = panelShown
        if (!capture.active) {
            // 先拉起授权页，再移走悬浮窗（悬浮窗可见时才允许从后台打开页面）
            CaptureConsentActivity.opened = false
            try {
                startActivity(Intent(this, CaptureConsentActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION))
            } catch (e: Exception) {
                toast("无法打开截屏授权：${e.message}")
                capturing = false
                return
            }
            hideAllWindows()
            // 有些系统（小米、OPPO、华为等）会静默拦截后台弹出页面：1.5 秒没打开就恢复浮窗并提示
            val token = ++consentToken
            val handler = android.os.Handler(mainLooper)
            handler.postDelayed({
                if (token == consentToken && capturing && !CaptureConsentActivity.opened) {
                    consentToken++
                    toast("截屏授权页被系统拦截了。请在系统设置里给「划词助手」打开「后台弹出界面」权限后再试")
                    restoreAfterCapture()
                }
            }, 1500)
            // 一分钟还没结果（比如在授权框上按了 Home）：放弃这次截屏
            handler.postDelayed({
                if (token == consentToken && capturing && !capture.active) {
                    consentToken++
                    restoreAfterCapture()
                }
            }, 60_000)
        } else {
            val keyboardWasUp = panelFocusable
            hideAllWindows()
            grabAndCrop(settleMs = if (keyboardWasUp) 450 else 300)
        }
    }

    private fun hideAllWindows() {
        if (panel != null) setPanelFocusable(false)
        removeSafely(panel, panelShown); panelShown = false
        hideBubble()
        hiddenAt = android.os.SystemClock.uptimeMillis()
    }

    private fun onCaptureConsent(intent: Intent) {
        consentToken++ // 作废超时检查
        if (!capturing) return // 已经超时放弃了
        val code = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
        @Suppress("DEPRECATION")
        val data: Intent? = intent.getParcelableExtra(EXTRA_RESULT_DATA)
        if (code != android.app.Activity.RESULT_OK || data == null) {
            toast("已取消截屏")
            restoreAfterCapture()
            return
        }
        // Android 14：拿到授权后，先以「屏幕投射」类型进入前台，再建立投射
        if (!goForeground(projection = true)) {
            toast("无法开始截屏，请再试一次")
            restoreAfterCapture()
            return
        }
        if (!capture.start(code, data)) {
            goForeground(projection = false)
            toast("无法开始截屏，请再试一次")
            restoreAfterCapture()
            return
        }
        // 等系统授权框的淡出动画走完再取画面
        grabAndCrop(settleMs = 600)
    }

    private fun grabAndCrop(settleMs: Long) {
        capture.grab(hiddenAt, settleMs) { bmp ->
            if (!running) return@grab
            if (bmp == null) {
                toast("没截到画面，请再试一次")
                restoreAfterCapture()
                return@grab
            }
            val r = scanRect
            if (r != null) {
                scanRect = null
                recognizeText(bmp, r)
                return@grab
            }
            cropOverlay = CropOverlay(ui, wm, bmp) { result ->
                cropOverlay = null
                if (running) {
                    if (result == null) restoreAfterCapture() else attachShot(result)
                }
            }.also { it.show() }
        }
    }

    private fun restoreAfterCapture() {
        if (!running) return
        capturing = false
        scanRect = null
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
                markClipSeen(txt)
                hideClipCard()
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
        pendingSources = null
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
                val nearBottom = scroll.scrollY + scroll.height >= body.height - dp(48)
                if (answer.isEmpty()) av.setTextColor(getColor(R.color.ink))
                answer.append(piece)
                av.text = Md.render(answer.toString())
                status.text = "正在回答…"
                if (nearBottom) scroll.post { scroll.scrollTo(0, body.height) }
            },
            onDone = { finishAnswer(null, stopped = false) },
            onError = { msg -> finishAnswer(msg, stopped = false) },
            onThinking = { n -> if (answer.isEmpty()) status.text = "深度思考中…（已想 $n 字）" },
            web = webOn(),
            onSearch = { q ->
                if (answer.isEmpty()) {
                    av.setTextColor(getColor(R.color.ink_soft))
                    av.text = "正在搜索：$q …"
                }
                status.text = "正在联网搜索：$q"
            },
            onReset = { answer.setLength(0) },
            onSources = { list -> pendingSources = list },
        )
    }

    // ---------- 联网 ----------

    /** 只有 DeepSeek 能联网 */
    private fun webOn() = Prefs.provider(this) == Prefs.DEEPSEEK && Prefs.webSearch(this)

    private fun updateWebBtn() {
        if (panel == null) return
        btnWeb.visibility = if (Prefs.provider(this) == Prefs.DEEPSEEK) View.VISIBLE else View.GONE
        val on = Prefs.webSearch(this)
        btnWeb.text = if (on) "联网 ✓" else "联网"
        btnWeb.setTextColor(getColor(if (on) R.color.accent else R.color.ink_soft))
    }

    /** 回答下面列出引用的来源，点标题用浏览器打开 */
    private fun addSourcesView(list: List<AiClient.SearchHit>) {
        val sb = SpannableStringBuilder("来源")
        sb.setSpan(StyleSpan(Typeface.BOLD), 0, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        sb.setSpan(ForegroundColorSpan(getColor(R.color.accent)), 0, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        val linkColor = getColor(R.color.accent)
        for (h in list) {
            sb.append("\n[").append(h.index.toString()).append("] ")
            val start = sb.length
            sb.append(h.title.ifBlank { h.url })
            val url = h.url
            sb.setSpan(object : ClickableSpan() {
                override fun onClick(widget: View) = openUrl(url)
                override fun updateDrawState(ds: TextPaint) {
                    ds.color = linkColor
                    ds.isUnderlineText = false
                }
            }, start, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            val meta = listOf(h.site, h.date.take(10)).filter { it.isNotBlank() }.joinToString(" · ")
            if (meta.isNotEmpty()) sb.append("  ").append(meta)
        }
        val tv = TextView(ui).apply {
            text = sb
            setTextColor(getColor(R.color.ink_soft))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 12.5f)
            setLineSpacing(dp(3).toFloat(), 1f)
            movementMethod = LinkMovementMethod.getInstance()
            highlightColor = android.graphics.Color.TRANSPARENT
        }
        thread.addView(tv, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dp(6) })
    }

    private fun openUrl(url: String) {
        try {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            minimizePanel() // 收起浮窗，免得挡住网页
        } catch (_: Exception) {
            copyText(url)
            toast("没有可用的浏览器，网址已复制")
        }
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
            else -> {
                status.text = ""
                pendingSources?.takeIf { it.isNotEmpty() && text.isNotBlank() }?.let { addSourcesView(it) }
            }
        }
        pendingSources = null
        scroll.post { scroll.scrollTo(0, body.height) }
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
        empty.visibility = if (has) View.GONE else View.VISIBLE
    }

    private fun copyAnswer() {
        val last = if (answer.isNotBlank()) answer.toString()
                   else history.lastOrNull { it.role == "assistant" }?.content.orEmpty()
        if (last.isBlank()) toast("还没有回答可以复制") else copyText(last)
    }

    private fun copyText(text: String) {
        markClipSeen(text) // 自己复制的不再提示
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
