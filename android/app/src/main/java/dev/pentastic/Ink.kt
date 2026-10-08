package dev.pentastic

import android.content.SharedPreferences
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Path
import android.graphics.PorterDuff
import android.graphics.PorterDuffXfermode
import kotlin.math.max

enum class Brush(val label: String) { PEN("Pen"), PENCIL("Pencil"), MARKER("Marker"), ERASER("Eraser") }

class ToolState(private val prefs: SharedPreferences) {
    var brush = Brush.entries.getOrNull(prefs.getInt("brush", 0))?.takeIf { it != Brush.ERASER } ?: Brush.PEN
    var eraser = false
    var shape = Shape.entries.getOrNull(prefs.getInt("shape", 0)) ?: Shape.NONE
    var color = prefs.getInt("color", COLORS[0]) or OPAQUE
    var opacity = prefs.getFloat("opacity", 1f).coerceIn(MIN_OPACITY, 1f)
    var sizeDp = prefs.getFloat("size_dp", 3f).coerceIn(MIN_SIZE_DP, MAX_SIZE_DP)
    val recent: MutableList<Int> = prefs.getString("recent", "")!!.split(',').mapNotNull { it.toLongOrNull(16)?.toInt() }.toMutableList()
    var keepInk = prefs.getBoolean("keep_ink", true)
    var grid = prefs.getBoolean("grid", true)
    var mode = prefs.getInt("mode", Protocol.MODE_CONTROL).coerceIn(Protocol.MODE_CONTROL, Protocol.MODE_DRAW)
    var paper = prefs.getInt("paper", Protocol.PAPER_WHITE).takeIf { it == Protocol.PAPER_BLACK } ?: Protocol.PAPER_WHITE
    var mirror = prefs.getBoolean("mirror", false)
    var feel = PressureFeel.entries.getOrNull(prefs.getInt("feel", 1)) ?: PressureFeel.NORMAL
    var smoothing = Smoothing.entries.getOrNull(prefs.getInt("smoothing", 0)) ?: Smoothing.OFF
    var penButton = PenButton.entries.getOrNull(prefs.getInt("pen_button", 0)) ?: PenButton.RIGHT_CLICK
    val keys: MutableList<Shortcut> = (prefs.getString("keys", null)?.split('|') ?: Shortcuts.DEFAULT).mapNotNull(Shortcuts::byLabel).toMutableList()
    var showKeys = prefs.getBoolean("show_keys", true)

    val active get() = if (eraser) Brush.ERASER else brush

    val inkColor get() = (Math.round(opacity * 255) shl 24) or (color and 0xFFFFFF)

    val paperColor get() = if (paper == Protocol.PAPER_BLACK) BLACK_PAPER else WHITE_PAPER

    fun useColor(c: Int) {
        color = c or OPAQUE
        recent.remove(color)
        recent.add(0, color)
        while (recent.size > RECENT) recent.removeAt(recent.size - 1)
    }

    fun save() = prefs.edit()
        .putInt("brush", brush.ordinal).putInt("shape", shape.ordinal).putInt("color", color).putFloat("opacity", opacity)
        .putFloat("size_dp", sizeDp).putString("recent", recent.joinToString(",") { Integer.toHexString(it) })
        .putBoolean("keep_ink", keepInk).putBoolean("grid", grid).putInt("mode", mode).putInt("paper", paper).putBoolean("mirror", mirror)
        .putInt("feel", feel.ordinal).putInt("smoothing", smoothing.ordinal).putInt("pen_button", penButton.ordinal).putString("keys", keys.joinToString("|") { it.label }).putBoolean("show_keys", showKeys)
        .apply()

    companion object {
        const val OPAQUE = 0xFF000000.toInt()
        const val MIN_SIZE_DP = 0.5f
        const val MAX_SIZE_DP = 30f
        const val MIN_OPACITY = 0.1f
        const val RECENT = 8
        const val WHITE_PAPER = 0xFFFFFFFF.toInt()
        const val BLACK_PAPER = 0xFF0C0C0E.toInt()

        val COLORS = intArrayOf(
            0xFF000000.toInt(), 0xFFFFFFFF.toInt(), 0xFF8A94A6.toInt(), 0xFFE5484D.toInt(), 0xFFF76B15.toInt(),
            0xFFFFC53D.toInt(), 0xFF30A46C.toInt(), 0xFF3E63DD.toInt(), 0xFF8E4EC6.toInt(),
        )
        val COLOR_NAMES = arrayOf("Black", "White", "Gray", "Red", "Orange", "Yellow", "Green", "Blue", "Violet")
    }
}

class Stroke(val brush: Brush, val color: Int, val width: Float) {
    private var pts = FloatArray(3 * 64)
    var count = 0
        private set

    fun add(x: Float, y: Float, p: Float) {
        if (3 * count + 3 > pts.size) pts = pts.copyOf(pts.size * 2)
        pts[3 * count] = x
        pts[3 * count + 1] = y
        pts[3 * count + 2] = p
        count++
    }

    fun x(i: Int) = pts[3 * i]
    fun y(i: Int) = pts[3 * i + 1]
    fun p(i: Int) = pts[3 * i + 2]
}

object StrokePainter {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND; strokeJoin = Paint.Join.ROUND
    }
    private val path = Path()
    private val clear = PorterDuffXfermode(PorterDuff.Mode.CLEAR)

    fun paint(c: Canvas, s: Stroke, w: Float, h: Float, from: Int = 0) {
        if (s.count == 0) return
        paint.color = s.color
        paint.xfermode = if (s.brush == Brush.ERASER) clear else null
        val seeThrough = Color.alpha(s.color) < 255
        when {
            s.brush == Brush.ERASER || (s.brush == Brush.PEN && !seeThrough) -> for (i in from until s.count) {
                val j = max(i - 1, 0)
                paint.strokeWidth = if (s.brush == Brush.ERASER) s.width * 4 else s.width * (0.3f + 0.7f * s.p(i))
                c.drawLine(s.x(j) * w, s.y(j) * h, s.x(i) * w, s.y(i) * h, paint)
            }
            else -> {
                paint.strokeWidth = s.width * when (s.brush) { Brush.MARKER -> 3f; Brush.PENCIL -> 0.6f; else -> 0.65f }
                paint.alpha = Color.alpha(s.color) * when (s.brush) { Brush.MARKER -> 96; Brush.PENCIL -> 200; else -> 255 } / 255
                path.rewind()
                path.moveTo(s.x(0) * w, s.y(0) * h)
                for (i in 1 until s.count) path.lineTo(s.x(i) * w, s.y(i) * h)
                if (s.count == 1) path.lineTo(s.x(0) * w + 0.1f, s.y(0) * h)
                c.drawPath(path, paint)
            }
        }
    }
}

class InkLayer {
    private val strokes = ArrayList<Stroke>()
    private val history = ArrayList<Any>()
    private var bitmap: Bitmap? = null
    private var canvas: Canvas? = null
    private var active: Stroke? = null
    private var zoom = PenMath.Zoom()
    private val view = Matrix()

    private val redoable = ArrayList<Any>()

    val isEmpty get() = strokes.isEmpty() && active == null
    val canUndo get() = history.isNotEmpty()
    val canRedo get() = redoable.isNotEmpty()

    fun commit(brush: Brush, color: Int, width: Float, points: List<Pair<Float, Float>>, pressure: Float) {
        begin(brush, color, width)
        for ((x, y) in points) add(x, y, pressure)
        end()
    }

    fun resize(w: Int, h: Int) {
        if (w <= 0 || h <= 0 || (bitmap?.width == w && bitmap?.height == h)) return
        bitmap?.recycle()
        bitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888).also { canvas = Canvas(it) }
        setZoom(zoom)
    }

    fun setZoom(z: PenMath.Zoom) {
        zoom = z
        val b = bitmap ?: return
        view.setTranslate(-z.x * b.width, -z.y * b.height)
        view.postScale(z.scale, z.scale)
        canvas!!.setMatrix(view)
        redraw()
    }

    fun begin(brush: Brush, color: Int, width: Float) {
        end()
        active = Stroke(brush, color, width)
    }

    fun add(x: Float, y: Float, p: Float) {
        val s = active ?: return
        s.add(x, y, p)
        if (s.brush == Brush.ERASER) paintInto(s, s.count - 1)
    }

    fun end() {
        val s = active ?: return
        active = null
        if (s.count == 0) return
        if (s.brush != Brush.ERASER) paintInto(s, 0)
        strokes += s
        history.add(s)
        redoable.clear()
    }

    fun undo() {
        val step = history.removeLastOrNull() ?: return
        when (step) {
            is Stroke -> strokes.remove(step)
            is List<*> -> strokes.addAll(step.filterIsInstance<Stroke>())
        }
        redoable.add(step)
        redraw()
    }

    fun redo() {
        val step = redoable.removeLastOrNull() ?: return
        when (step) {
            is Stroke -> strokes.add(step)
            is List<*> -> strokes.removeAll(step.filterIsInstance<Stroke>().toSet())
        }
        history.add(step)
        redraw()
    }

    fun reset() {
        end()
        strokes.clear()
        history.clear()
        redoable.clear()
        redraw()
    }

    fun clear() {
        if (strokes.isEmpty()) return
        history.add(ArrayList(strokes))
        redoable.clear()
        strokes.clear()
        redraw()
    }

    fun drawOn(c: Canvas, left: Float, top: Float) {
        val b = bitmap ?: return
        c.drawBitmap(b, left, top, null)
        val s = active ?: return
        if (s.brush == Brush.ERASER) return
        c.save()
        c.translate(left, top)
        c.concat(view)
        StrokePainter.paint(c, s, b.width.toFloat(), b.height.toFloat())
        c.restore()
    }

    private fun paintInto(s: Stroke, from: Int) {
        val b = bitmap ?: return
        StrokePainter.paint(canvas!!, s, b.width.toFloat(), b.height.toFloat(), from)
    }

    private fun redraw() {
        val b = bitmap ?: return
        b.eraseColor(Color.TRANSPARENT)
        for (s in strokes) StrokePainter.paint(canvas!!, s, b.width.toFloat(), b.height.toFloat())
    }
}
