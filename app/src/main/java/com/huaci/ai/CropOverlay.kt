package com.huaci.ai

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.RectF
import android.graphics.Typeface
import android.os.Build
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * 全屏的截图选框：在定格的屏幕画面上拖出一个区域。
 *  - 拖框内部：移动
 *  - 拖四个角或四条边：调整大小
 *  - 在框外按下拖动：重新画一个框
 * 点「放进对话框」返回裁好的图片；点「取消」返回 null。
 */
class CropOverlay(
    private val ctx: Context,
    private val wm: WindowManager,
    private val shot: Bitmap,
    private val onDone: (Bitmap?) -> Unit,
) {
    private val dm = ctx.resources.displayMetrics
    private fun dp(v: Float) = v * dm.density
    private fun dpi(v: Int) = (v * dm.density + 0.5f).toInt()

    private val root = FrameLayout(ctx)
    private val crop = CropView(ctx)
    private var shown = false
    private var finished = false

    init {
        root.addView(crop, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT
        ))

        // 顶部提示
        val hint = TextView(ctx).apply {
            text = "拖动选框 · 拖角或边调大小 · 框外拖动重画"
            setTextColor(Color.WHITE)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setPadding(dpi(14), dpi(7), dpi(14), dpi(7))
            background = pill(0xB3000000.toInt())
        }
        root.addView(hint, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.CENTER_HORIZONTAL
        ).apply { topMargin = statusBarH() + dpi(10) })

        // 底部按钮
        val bar = LinearLayout(ctx).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
        }
        bar.addView(button("取消", primary = false) { finish(null) })
        bar.addView(button("全屏", primary = false) { crop.selectAll() })
        bar.addView(button("放进对话框", primary = true) { finish(crop.cropped()) })
        root.addView(bar, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
        ).apply { bottomMargin = navBarH() + dpi(22) })
    }

    fun show() {
        val lp = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            if (Build.VERSION.SDK_INT >= 28) {
                layoutInDisplayCutoutMode =
                    WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            }
        }
        try {
            wm.addView(root, lp)
            shown = true
        } catch (_: Exception) {
            finish(null)
        }
    }

    fun dismiss() {
        if (shown) try { wm.removeView(root) } catch (_: Exception) {}
        shown = false
    }

    private fun finish(result: Bitmap?) {
        if (finished) return
        finished = true
        dismiss()
        onDone(result)
    }

    private fun button(label: String, primary: Boolean, onClick: () -> Unit) = TextView(ctx).apply {
        text = label
        gravity = Gravity.CENTER
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
        typeface = if (primary) Typeface.DEFAULT_BOLD else Typeface.DEFAULT
        setTextColor(if (primary) ctx.getColor(R.color.accent_ink) else Color.WHITE)
        background = pill(if (primary) ctx.getColor(R.color.accent) else 0xCC1A1F1C.toInt())
        setPadding(dpi(20), 0, dpi(20), 0)
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, dpi(46)
        ).apply { marginStart = dpi(5); marginEnd = dpi(5) }
        setOnClickListener { onClick() }
    }

    private fun pill(color: Int) = android.graphics.drawable.GradientDrawable().apply {
        cornerRadius = dp(999f)
        setColor(color)
    }

    private fun statusBarH(): Int {
        val id = ctx.resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) ctx.resources.getDimensionPixelSize(id) else dpi(24)
    }

    private fun navBarH(): Int {
        val id = ctx.resources.getIdentifier("navigation_bar_height", "dimen", "android")
        return if (id > 0) ctx.resources.getDimensionPixelSize(id) else dpi(16)
    }

    // ======================= 选框视图 =======================

    private inner class CropView(context: Context) : View(context) {
        private val rect = RectF()
        private var initialized = false
        private val loc = IntArray(2)

        private val dim = Paint().apply { color = 0x8C000000.toInt() }
        private val frame = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = dp(2f)
            color = context.getColor(R.color.accent)
        }
        private val guide = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = dp(1f)
            color = 0x66FFFFFF
        }
        private val knob = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE
        }
        private val knobRing = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = dp(2f)
            color = context.getColor(R.color.accent)
        }
        private val label = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE
            textSize = dp(12f)
        }
        private val labelBg = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xB3000000.toInt() }

        private val touchR = dp(26f)
        private val minSize = dp(48f)

        // 拖动模式
        private val NONE = 0; private val MOVE = 1; private val DRAW = 2
        private var mode = NONE
        private var edgeL = false; private var edgeT = false; private var edgeR = false; private var edgeB = false
        private var lastX = 0f; private var lastY = 0f
        private var anchorX = 0f; private var anchorY = 0f

        override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
            super.onSizeChanged(w, h, oldw, oldh)
            if (!initialized && w > 0 && h > 0) {
                val bw = w * 0.84f; val bh = min(h * 0.3f, bw)
                rect.set((w - bw) / 2, h * 0.32f, (w + bw) / 2, h * 0.32f + bh)
                initialized = true
            }
        }

        fun selectAll() {
            rect.set(0f, 0f, width.toFloat(), height.toFloat())
            invalidate()
        }

        /** 把选框（视图坐标）换算成截图上的像素区域并裁出来。 */
        fun cropped(): Bitmap? {
            getLocationOnScreen(loc)
            val l = (rect.left + loc[0]).roundToInt().coerceIn(0, shot.width - 1)
            val t = (rect.top + loc[1]).roundToInt().coerceIn(0, shot.height - 1)
            val r = (rect.right + loc[0]).roundToInt().coerceIn(l + 1, shot.width)
            val b = (rect.bottom + loc[1]).roundToInt().coerceIn(t + 1, shot.height)
            return try { Bitmap.createBitmap(shot, l, t, r - l, b - t) } catch (_: Exception) { null }
        }

        override fun onDraw(canvas: Canvas) {
            getLocationOnScreen(loc)
            // 定格的屏幕画面（对齐到屏幕坐标）
            canvas.drawBitmap(shot, -loc[0].toFloat(), -loc[1].toFloat(), null)

            // 选框外变暗
            val w = width.toFloat(); val h = height.toFloat()
            canvas.drawRect(0f, 0f, w, rect.top, dim)
            canvas.drawRect(0f, rect.bottom, w, h, dim)
            canvas.drawRect(0f, rect.top, rect.left, rect.bottom, dim)
            canvas.drawRect(rect.right, rect.top, w, rect.bottom, dim)

            // 三分线
            val tw = rect.width() / 3; val th = rect.height() / 3
            for (i in 1..2) {
                canvas.drawLine(rect.left + tw * i, rect.top, rect.left + tw * i, rect.bottom, guide)
                canvas.drawLine(rect.left, rect.top + th * i, rect.right, rect.top + th * i, guide)
            }
            canvas.drawRect(rect, frame)

            // 四角 + 四边中点的手柄
            val r = dp(7f)
            val pts = floatArrayOf(
                rect.left, rect.top, rect.right, rect.top, rect.left, rect.bottom, rect.right, rect.bottom,
                rect.centerX(), rect.top, rect.centerX(), rect.bottom, rect.left, rect.centerY(), rect.right, rect.centerY()
            )
            for (i in pts.indices step 2) {
                val rr = if (i < 8) r else r * 0.75f
                canvas.drawCircle(pts[i], pts[i + 1], rr, knob)
                canvas.drawCircle(pts[i], pts[i + 1], rr, knobRing)
            }

            // 尺寸标签（实际像素）
            val txt = "${rect.width().roundToInt()} × ${rect.height().roundToInt()}"
            val tw2 = label.measureText(txt)
            val lx = rect.left
            val ly = if (rect.top > dp(30f)) rect.top - dp(8f) else rect.bottom + dp(20f)
            canvas.drawRoundRect(lx, ly - dp(15f), lx + tw2 + dp(12f), ly + dp(5f), dp(6f), dp(6f), labelBg)
            canvas.drawText(txt, lx + dp(6f), ly, label)
        }

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            val x = e.x; val y = e.y
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    edgeL = abs(x - rect.left) < touchR && y in (rect.top - touchR)..(rect.bottom + touchR)
                    edgeR = abs(x - rect.right) < touchR && y in (rect.top - touchR)..(rect.bottom + touchR)
                    edgeT = abs(y - rect.top) < touchR && x in (rect.left - touchR)..(rect.right + touchR)
                    edgeB = abs(y - rect.bottom) < touchR && x in (rect.left - touchR)..(rect.right + touchR)
                    // 框很小时左右/上下可能同时命中，取更近的一边
                    if (edgeL && edgeR) { if (abs(x - rect.left) < abs(x - rect.right)) edgeR = false else edgeL = false }
                    if (edgeT && edgeB) { if (abs(y - rect.top) < abs(y - rect.bottom)) edgeB = false else edgeT = false }
                    mode = when {
                        edgeL || edgeR || edgeT || edgeB -> MOVE
                        rect.contains(x, y) -> MOVE
                        else -> { anchorX = x; anchorY = y; DRAW }
                    }
                    lastX = x; lastY = y
                    return true
                }
                MotionEvent.ACTION_MOVE -> {
                    val w = width.toFloat(); val h = height.toFloat()
                    val dx = x - lastX; val dy = y - lastY
                    lastX = x; lastY = y
                    if (mode == DRAW) {
                        if (abs(x - anchorX) > minSize / 2 || abs(y - anchorY) > minSize / 2) {
                            rect.set(
                                min(anchorX, x).coerceAtLeast(0f), min(anchorY, y).coerceAtLeast(0f),
                                max(anchorX, x).coerceAtMost(w), max(anchorY, y).coerceAtMost(h)
                            )
                            ensureMin(w, h)
                        }
                    } else if (mode == MOVE) {
                        if (edgeL || edgeR || edgeT || edgeB) {
                            if (edgeL) rect.left = (rect.left + dx).coerceIn(0f, rect.right - minSize)
                            if (edgeR) rect.right = (rect.right + dx).coerceIn(rect.left + minSize, w)
                            if (edgeT) rect.top = (rect.top + dy).coerceIn(0f, rect.bottom - minSize)
                            if (edgeB) rect.bottom = (rect.bottom + dy).coerceIn(rect.top + minSize, h)
                        } else {
                            val ox = dx.coerceIn(-rect.left, w - rect.right)
                            val oy = dy.coerceIn(-rect.top, h - rect.bottom)
                            rect.offset(ox, oy)
                        }
                    }
                    invalidate()
                    return true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    mode = NONE
                    return true
                }
            }
            return super.onTouchEvent(e)
        }

        private fun ensureMin(w: Float, h: Float) {
            if (rect.width() < minSize) rect.right = min(rect.left + minSize, w).also { if (it - rect.left < minSize) rect.left = it - minSize }
            if (rect.height() < minSize) rect.bottom = min(rect.top + minSize, h).also { if (it - rect.top < minSize) rect.top = it - minSize }
        }
    }
}
