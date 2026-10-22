package com.huaci.ai

import android.content.Context
import android.util.AttributeSet
import android.widget.ScrollView

/** 内容少时多高就多高，超过 maxHeightPx 后在里面滚动。 */
class MaxHeightScrollView @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null
) : ScrollView(context, attrs) {

    var maxHeightPx: Int = (context.resources.displayMetrics.density * 240).toInt()
        set(value) { field = value; requestLayout() }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val mode = MeasureSpec.getMode(heightMeasureSpec)
        val size = MeasureSpec.getSize(heightMeasureSpec)
        val limit = if (mode == MeasureSpec.UNSPECIFIED) maxHeightPx else minOf(size, maxHeightPx)
        super.onMeasure(widthMeasureSpec, MeasureSpec.makeMeasureSpec(limit, MeasureSpec.AT_MOST))
    }
}
