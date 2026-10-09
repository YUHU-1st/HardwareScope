package io.github.hardwarescope.mobile

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.view.View
import java.text.DecimalFormat

class HistoryChart(context: Context) : View(context) {
    var values: List<Double?> = emptyList()
        set(value) { field = value; invalidate() }
    var unit: String = ""
        set(value) { field = value; invalidate() }
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val path = Path()
    private val number = DecimalFormat("0.#")
    private val density = resources.displayMetrics.density

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val left = 48f * density
        val right = width - 12f * density
        val top = 16f * density
        val bottom = height - 28f * density
        val known = values.filterNotNull()
        val max = if (unit == "%") maxOf(100.0, known.maxOrNull() ?: 100.0) else maxOf(1.0, (known.maxOrNull() ?: 1.0) * 1.15)
        paint.textSize = 10f * density
        for (i in 0..4) {
            val y = bottom - (bottom - top) * i / 4
            paint.color = Color.rgb(48, 59, 73); paint.strokeWidth = density
            canvas.drawLine(left, y, right, y, paint)
            paint.color = Color.rgb(149, 163, 184)
            canvas.drawText(number.format(max * i / 4), 3f * density, y + 4f * density, paint)
        }
        canvas.drawText("较早", left, height - 7f * density, paint)
        canvas.drawText("当前 $unit", right - 50f * density, height - 7f * density, paint)
        if (known.isEmpty()) {
            paint.textSize = 13f * density
            canvas.drawText("尚无可用读数", left + 25f * density, (top + bottom) / 2, paint)
            return
        }
        path.reset()
        var connected = false
        values.forEachIndexed { index, value ->
            if (value == null) { connected = false; return@forEachIndexed }
            val x = left + (right - left) * index / maxOf(1, values.size - 1)
            val y = bottom - ((bottom - top) * (value / max).coerceIn(0.0, 1.0)).toFloat()
            if (!connected) path.moveTo(x, y) else path.lineTo(x, y)
            connected = true
            if (values.size == 1) { paint.color = Color.rgb(96, 212, 190); canvas.drawCircle(x, y, 3f * density, paint) }
        }
        paint.color = Color.rgb(96, 212, 190); paint.style = Paint.Style.STROKE; paint.strokeWidth = 2.4f * density
        canvas.drawPath(path, paint)
        paint.style = Paint.Style.FILL
    }
}
