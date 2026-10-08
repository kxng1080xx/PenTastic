package dev.pentastic

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import android.view.View
import kotlin.math.floor
import kotlin.math.hypot
import kotlin.math.log2
import kotlin.math.roundToInt

class PenView(
    context: Context,
    private val tools: ToolState,
    val inks: Array<InkLayer>,
    private val send: (List<Sample>) -> Unit,
    private val onStrokeEnd: () -> Unit,
    private val onPenUndo: () -> Unit,
) : View(context) {
    var target: TargetInfo? = null
        set(value) {
            field = value
            relayout()
        }

    var status = ""
    var page = ""
        set(value) {
            field = value
            invalidate()
        }

    var onBoxChanged: ((PenMath.Box) -> Unit)? = null

    var zoom = PenMath.Zoom()
        private set
    var onZoomChanged: (() -> Unit)? = null

    var mirrorAvailable = true
        set(value) {
            if (field == value) return
            field = value
            invalidate()
        }

    var fingersArePalm = false

    var leftInset = 0f
        set(value) {
            if (field == value) return
            field = value
            relayout()
        }

    var rightInset = 0f
        set(value) {
            if (field == value) return
            field = value
            relayout()
        }

    private val dp = resources.displayMetrics.density
    private val ink get() = inks[tools.mode]

    val padWidth get() = box.width
    val padHeight get() = box.height
    private var box = PenMath.Box(0f, 0f, 1f, 1f)
    private val hasTilt = HashMap<Int, Boolean>()
    private val targetW get() = target?.width ?: 1920
    private val targetH get() = target?.height ?: 1080

    private class Trail(val x: Float, val y: Float, val w: Float, val t: Long, val stroke: Int)
    private val trail = ArrayDeque<Trail>()
    private var stroke = 0
    private var cursorX = 0f
    private var cursorY = 0f
    private var cursorTargetX = 0f
    private var cursorTargetY = 0f
    private var cursorP = 0f
    private var cursorVisible = false
    private var cursorContact = false
    private var cursorErasing = false
    private var last: Sample? = null
    private var pinchFrom: PenMath.Zoom? = null
    private var pinchX = 0f
    private var pinchY = 0f
    private var pinchSpan = 0f
    private val stabilizer = PenTools.Stabilizer()
    private var barrelWasDown = false
    private var shapeFrom: Pair<Float, Float>? = null
    private var shapeTo: Pair<Float, Float>? = null

    private val pendingExit = Runnable {
        last?.let { send(listOf(it.copy(kind = Kind.HOVER_EXIT, pressure = 0))) }
        cursorVisible = false
        invalidate()
    }

    private val padPaint = Paint().apply { color = PAD }
    private val tickPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = TICK; style = Paint.Style.STROKE; strokeWidth = 2 * dp
    }
    private val gridPaint = Paint().apply { color = GRID; strokeWidth = 1 * dp }
    private val gridMidPaint = Paint().apply { color = GRID_MID; strokeWidth = 1 * dp }
    private val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = TICK; textSize = 10 * dp * resources.configuration.fontScale
    }
    private val inkPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { strokeCap = Paint.Cap.ROUND }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Toolbar.TEXT; textSize = 13 * dp * resources.configuration.fontScale
        textAlign = Paint.Align.CENTER
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        requestUnbufferedDispatch(InputDevice.SOURCE_CLASS_POINTER)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) = relayout()

    private fun relayout() {
        val b = PenMath.letterbox(width - leftInset - rightInset, height.toFloat(), targetW, targetH, 4 * dp)
        box = b.copy(left = b.left + leftInset)
        onBoxChanged?.invoke(box)
        for (ink in inks) ink.resize(box.width.toInt(), box.height.toInt())
        invalidate()
    }

    override fun onTouchEvent(e: MotionEvent): Boolean {
        val i = stylusIndex(e) ?: return pinch(e)
        val kind = when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> Kind.DOWN
            MotionEvent.ACTION_POINTER_DOWN -> if (e.actionIndex == i) Kind.DOWN else return true
            MotionEvent.ACTION_MOVE -> Kind.MOVE
            MotionEvent.ACTION_UP -> Kind.UP
            MotionEvent.ACTION_POINTER_UP -> if (e.actionIndex == i) Kind.UP else return true
            MotionEvent.ACTION_CANCEL -> Kind.CANCEL
            else -> return true
        }
        if (kind == Kind.DOWN) removeCallbacks(pendingExit)
        emit(e, i, kind)
        return true
    }

    override fun onGenericMotionEvent(e: MotionEvent): Boolean {
        val i = stylusIndex(e) ?: return super.onGenericMotionEvent(e)
        when (e.actionMasked) {
            MotionEvent.ACTION_HOVER_ENTER, MotionEvent.ACTION_HOVER_MOVE -> {
                removeCallbacks(pendingExit)
                emit(e, i, Kind.HOVER)
            }
            MotionEvent.ACTION_HOVER_EXIT -> postDelayed(pendingExit, 80)
            else -> return super.onGenericMotionEvent(e)
        }
        return true
    }

    private fun emit(e: MotionEvent, i: Int, kind: Int) {
        val tilt = hasTilt.getOrPut(e.deviceId) { e.device?.getMotionRange(MotionEvent.AXIS_TILT) != null }
        val barrel = e.buttonState and MotionEvent.BUTTON_STYLUS_PRIMARY != 0
        if (barrel && !barrelWasDown && tools.penButton == PenButton.UNDO) onPenUndo()
        barrelWasDown = barrel
        val erasing = tools.eraser || e.getToolType(i) == MotionEvent.TOOL_TYPE_ERASER || (barrel && tools.penButton == PenButton.ERASER)
        var flags = if (tilt) Flag.TILT_VALID else 0
        if (barrel && tools.penButton == PenButton.RIGHT_CLICK) flags = flags or Flag.BARREL
        if (erasing) flags = flags or Flag.ERASER
        val shaping = tools.shape != Shape.NONE && !erasing
        val contact = kind == Kind.DOWN || kind == Kind.MOVE
        val sizePx = tools.sizeDp * dp
        if (kind == Kind.DOWN) {
            stroke++
            pinchFrom = null
            stabilizer.follow = tools.smoothing.follow
            stabilizer.reset()
            if (tools.keepInk && !shaping) ink.begin(if (erasing) Brush.ERASER else tools.brush, tools.inkColor, sizePx / zoom.scale)
        }

        val n = e.historySize
        val out = ArrayList<Sample>(n + 1)
        for (h in 0..n) {
            val current = h == n
            val x = if (current) e.getX(i) else e.getHistoricalX(i, h)
            val y = if (current) e.getY(i) else e.getHistoricalY(i, h)
            val p = if (current) e.getPressure(i) else e.getHistoricalPressure(i, h)
            val time = if (current) e.eventTime else e.getHistoricalEventTime(h)
            val k = if (current) kind else if (contact || kind == Kind.UP || kind == Kind.CANCEL) Kind.MOVE else Kind.HOVER
            val (tx, ty) = if (tilt) PenMath.tilt(
                if (current) e.getAxisValue(MotionEvent.AXIS_TILT, i) else e.getHistoricalAxisValue(MotionEvent.AXIS_TILT, i, h),
                if (current) e.getAxisValue(MotionEvent.AXIS_ORIENTATION, i) else e.getHistoricalAxisValue(MotionEvent.AXIS_ORIENTATION, i, h),
            ) else 0 to 0
            val inContact = k == Kind.DOWN || k == Kind.MOVE
            val (sx, sy) = if (k == Kind.MOVE) stabilizer.next(x, y) else (x to y).also { if (k == Kind.DOWN) stabilizer.next(x, y) }
            val padX = ((sx - box.left) / box.width).coerceIn(0f, 1f)
            val padY = ((sy - box.top) / box.height).coerceIn(0f, 1f)
            val s = Sample(
                k, flags, PenMath.unit(zoom.targetX(padX)), PenMath.unit(zoom.targetY(padY)),
                if (inContact) PenMath.unit(PenTools.pressure(p, tools.feel)) else 0, tx, ty, time and 0xFFFFFFFFL,
            )
            val shapePoint = shaping && (inContact || k == Kind.UP)
            if (!shapePoint) out += s
            else {
                if (k == Kind.DOWN || shapeFrom == null) shapeFrom = s.x / 65535f to s.y / 65535f
                shapeTo = s.x / 65535f to s.y / 65535f
            }

            val nx = s.x / 65535f
            val ny = s.y / 65535f
            val pressure = s.pressure / 65535f
            cursorX = box.left + padX * box.width
            cursorY = box.top + padY * box.height
            cursorTargetX = nx
            cursorTargetY = ny
            cursorP = pressure
            cursorVisible = k != Kind.CANCEL
            cursorContact = inContact
            cursorErasing = erasing
            if (inContact && !shaping) {
                if (tools.keepInk) ink.add(nx, ny, pressure)
                else if (!erasing) trail.addLast(Trail(cursorX, cursorY, sizePx * (0.3f + 0.7f * pressure), time, stroke))
            }
        }
        if (out.isNotEmpty()) {
            last = out.last()
            send(out)
        }
        if (shaping && kind == Kind.UP) finishShape(flags, sizePx, e.eventTime)
        if (kind == Kind.CANCEL) shapeFrom = null
        if (kind == Kind.UP || kind == Kind.CANCEL) {
            ink.end()
            onStrokeEnd()
        }
        invalidate()
    }

    private fun finishShape(flags: Int, sizePx: Float, time: Long) {
        val (x0, y0) = shapeFrom ?: return
        val (x1, y1) = shapeTo ?: return
        shapeFrom = null
        shapeTo = null
        val pressure = 0.7f
        for (line in PenTools.shape(tools.shape, x0, y0, x1, y1, targetW.toFloat() / targetH)) {
            val points = PenTools.densify(line, 0.004f)
            if (tools.keepInk) ink.commit(tools.brush, tools.inkColor, sizePx / zoom.scale, points, pressure)
            send(points.mapIndexed { n, (x, y) ->
                val kind = when (n) { 0 -> Kind.DOWN; points.lastIndex -> Kind.UP; else -> Kind.MOVE }
                Sample(kind, flags, PenMath.unit(x), PenMath.unit(y), if (kind == Kind.UP) 0 else PenMath.unit(pressure), 0, 0, time and 0xFFFFFFFFL)
            })
        }
    }

    private fun drawShapePreview(c: Canvas) {
        val (x0, y0) = shapeFrom ?: return
        val (x1, y1) = shapeTo ?: return
        inkPaint.style = Paint.Style.STROKE
        inkPaint.color = tools.inkColor
        inkPaint.strokeWidth = (tools.sizeDp * dp * 0.8f).coerceAtLeast(1f)
        for (line in PenTools.shape(tools.shape, x0, y0, x1, y1, targetW.toFloat() / targetH)) {
            for ((a, b) in line.zipWithNext()) {
                c.drawLine(screenX(a.first), screenY(a.second), screenX(b.first), screenY(b.second), inkPaint)
            }
        }
    }

    private fun screenX(targetX: Float) = box.left + (targetX - zoom.x) * zoom.scale * box.width
    private fun screenY(targetY: Float) = box.top + (targetY - zoom.y) * zoom.scale * box.height

    private fun pinch(e: MotionEvent): Boolean {
        if (fingersArePalm || cursorContact) {
            pinchFrom = null
            return true
        }
        when (e.actionMasked) {
            MotionEvent.ACTION_POINTER_DOWN, MotionEvent.ACTION_POINTER_UP -> {
                val lifting = if (e.actionMasked == MotionEvent.ACTION_POINTER_UP) e.actionIndex else -1
                val down = (0 until e.pointerCount).filter { it != lifting }
                pinchFrom = null
                if (down.size >= 2) {
                    pinchFrom = zoom
                    pinchX = centerX(e, down[0], down[1])
                    pinchY = centerY(e, down[0], down[1])
                    pinchSpan = span(e, down[0], down[1])
                }
            }
            MotionEvent.ACTION_MOVE -> {
                val from = pinchFrom ?: return true
                if (e.pointerCount < 2 || pinchSpan < 1f) return true
                setZoom(PenMath.pinch(from, pinchX, pinchY, pinchSpan, centerX(e, 0, 1), centerY(e, 0, 1), span(e, 0, 1)))
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> pinchFrom = null
        }
        return true
    }

    private fun centerX(e: MotionEvent, a: Int, b: Int) = ((e.getX(a) + e.getX(b)) / 2 - box.left) / box.width
    private fun centerY(e: MotionEvent, a: Int, b: Int) = ((e.getY(a) + e.getY(b)) / 2 - box.top) / box.height
    private fun span(e: MotionEvent, a: Int, b: Int) = hypot(e.getX(a) - e.getX(b), e.getY(a) - e.getY(b))

    private fun setZoom(z: PenMath.Zoom) {
        if (z == zoom) return
        zoom = z
        for (ink in inks) ink.setZoom(z)
        trail.clear()
        onZoomChanged?.invoke()
        invalidate()
    }

    private fun stylusIndex(e: MotionEvent): Int? = (0 until e.pointerCount).firstOrNull {
        val tool = e.getToolType(it)
        tool == MotionEvent.TOOL_TYPE_STYLUS || tool == MotionEvent.TOOL_TYPE_ERASER
    }

    override fun onDraw(canvas: Canvas) {
        val mirroring = tools.mirror && mirrorAvailable
        if (mirroring) {
            canvas.save()
            canvas.clipOutRect(box.left, box.top, box.left + box.width, box.top + box.height)
            canvas.drawColor(GUTTER)
            canvas.restore()
        } else canvas.drawColor(GUTTER)
        val l = box.left; val t = box.top; val r = l + box.width; val b = t + box.height
        if (!mirroring) {
            padPaint.color = tools.paperColor
            canvas.drawRect(l, t, r, b, padPaint)
        }
        if (tools.grid) drawGrid(canvas, l, t, r, b)

        val s = 18 * dp; val inset = 6 * dp
        for ((cx, cy, dx, dy) in listOf(
            listOf(l + inset, t + inset, 1f, 1f), listOf(r - inset, t + inset, -1f, 1f),
            listOf(l + inset, b - inset, 1f, -1f), listOf(r - inset, b - inset, -1f, -1f),
        )) {
            canvas.drawLine(cx, cy, cx + dx * s, cy, tickPaint)
            canvas.drawLine(cx, cy, cx, cy + dy * s, tickPaint)
        }

        if (tools.keepInk) {
            canvas.save()
            canvas.clipRect(l, t, r, b)
            ink.drawOn(canvas, l, t)
            canvas.restore()
        } else drawTrail(canvas)

        drawShapePreview(canvas)
        if (cursorVisible) drawCursor(canvas)

        val readout = if (cursorVisible) {
            "pen ${(cursorTargetX * (targetW - 1)).roundToInt()}, ${(cursorTargetY * (targetH - 1)).roundToInt()}"
        } else "$targetW × $targetH"
        val mode = if (tools.mode == Protocol.MODE_DRAW) "Draw mode" else ""
        val pageText = if (tools.mode == Protocol.MODE_DRAW) page else ""
        val zoomText = if (zoom.scale > 1.01f) "zoom ${"%.1f".format(zoom.scale)}×" else ""
        val line = listOf(status, mode, pageText, zoomText, readout).filter { it.isNotEmpty() }.joinToString("   ·   ")
        textPaint.color = if (!mirroring && tools.paper == Protocol.PAPER_WHITE) PAD else Toolbar.TEXT
        if (mirroring) textPaint.setShadowLayer(3 * dp, 0f, 0f, 0xCC000000.toInt()) else textPaint.clearShadowLayer()
        canvas.drawText(line, l + box.width / 2, height - 12 * dp, textPaint)
    }

    private fun drawGrid(c: Canvas, l: Float, t: Float, r: Float, b: Float) {
        val n = 4 shl floor(log2(zoom.scale)).toInt()
        for (q in 1 until n) {
            val f = q.toFloat() / n
            val x = l + (f - zoom.x) * zoom.scale * box.width
            val y = t + (f - zoom.y) * zoom.scale * box.height
            val paint = if (2 * q == n) gridMidPaint else gridPaint
            if (x > l && x < r) {
                c.drawLine(x, t, x, b, paint)
                c.drawText("${targetW * q / n}", x + 4 * dp, t + 14 * dp, labelPaint)
            }
            if (y > t && y < b) {
                c.drawLine(l, y, r, y, paint)
                c.drawText("${targetH * q / n}", l + 6 * dp, y - 4 * dp, labelPaint)
            }
        }
    }

    private fun drawTrail(c: Canvas) {
        val now = SystemClock.uptimeMillis()
        while (trail.isNotEmpty() && now - trail.first().t > TRAIL_MS) trail.removeFirst()
        inkPaint.style = Paint.Style.STROKE
        inkPaint.color = tools.inkColor
        for (k in 1 until trail.size) {
            val a = trail[k - 1]; val b = trail[k]
            if (a.stroke != b.stroke) continue
            inkPaint.alpha = ((1 - (now - b.t).toFloat() / TRAIL_MS).coerceIn(0f, 1f) * android.graphics.Color.alpha(tools.inkColor)).toInt()
            inkPaint.strokeWidth = b.w
            c.drawLine(a.x, a.y, b.x, b.y, inkPaint)
        }
        if (trail.isNotEmpty()) postInvalidateOnAnimation()
    }

    private fun drawCursor(c: Canvas) {
        val sizePx = tools.sizeDp * dp
        inkPaint.alpha = 255
        if (cursorErasing) {
            inkPaint.color = Toolbar.TEXT
            inkPaint.style = Paint.Style.STROKE
            inkPaint.strokeWidth = 1.5f * dp
            c.drawCircle(cursorX, cursorY, sizePx * 2, inkPaint)
            return
        }
        inkPaint.color = tools.inkColor
        if (cursorContact) {
            inkPaint.style = Paint.Style.FILL
            c.drawCircle(cursorX, cursorY, (1.5f * dp).coerceAtLeast(sizePx * (0.15f + 0.35f * cursorP)), inkPaint)
        } else {
            inkPaint.style = Paint.Style.STROKE
            inkPaint.strokeWidth = 1.5f * dp
            c.drawCircle(cursorX, cursorY, 6 * dp, inkPaint)
            inkPaint.color = TICK
            inkPaint.strokeWidth = 0.75f * dp
            c.drawCircle(cursorX, cursorY, 7.5f * dp, inkPaint)
        }
    }

    private companion object {
        const val TRAIL_MS = 700
        const val GUTTER = 0xFF121A26.toInt()
        const val PAD = 0xFF22304A.toInt()
        const val TICK = 0xFF6F86AD.toInt()
        const val GRID = 0x336F86AD
        const val GRID_MID = 0x666F86AD
    }
}
