package dev.pentastic

import kotlin.math.atan2
import kotlin.math.ceil
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.sin

enum class Shape(val label: String) { NONE("Freehand"), LINE("Line"), ARROW("Arrow"), RECT("Rectangle"), ELLIPSE("Ellipse") }

enum class PressureFeel(val label: String, val gamma: Float) { SOFT("Soft", 0.6f), NORMAL("Normal", 1f), FIRM("Firm", 1.6f) }

enum class Smoothing(val label: String, val follow: Float) { OFF("Off", 1f), LIGHT("Light", 0.5f), STRONG("Strong", 0.22f) }

enum class PenButton(val label: String) { RIGHT_CLICK("Right-click"), ERASER("Eraser"), UNDO("Undo") }

object PenTools {
    fun pressure(p: Float, feel: PressureFeel): Float = p.coerceIn(0f, 1f).pow(feel.gamma)

    class Stabilizer {
        private var x = Float.NaN
        private var y = 0f
        var follow = 1f

        fun reset() {
            x = Float.NaN
        }

        fun next(px: Float, py: Float): Pair<Float, Float> {
            if (x.isNaN() || follow >= 1f) {
                x = px
                y = py
            } else {
                x += (px - x) * follow
                y += (py - y) * follow
            }
            return x to y
        }
    }

    fun shape(shape: Shape, x0: Float, y0: Float, x1: Float, y1: Float, aspect: Float): List<List<Pair<Float, Float>>> = when (shape) {
        Shape.NONE -> emptyList()
        Shape.LINE -> listOf(listOf(x0 to y0, x1 to y1))
        Shape.RECT -> listOf(listOf(x0 to y0, x1 to y0, x1 to y1, x0 to y1, x0 to y0))
        Shape.ELLIPSE -> {
            val cx = (x0 + x1) / 2
            val cy = (y0 + y1) / 2
            val rx = (x1 - x0) / 2
            val ry = (y1 - y0) / 2
            listOf((0..64).map { i ->
                val a = 2 * Math.PI * i / 64
                (cx + rx * cos(a)).toFloat() to (cy + ry * sin(a)).toFloat()
            })
        }
        Shape.ARROW -> {
            val dx = (x1 - x0) * aspect
            val dy = y1 - y0
            val length = hypot(dx, dy)
            val head = min(length * 0.3f, 0.05f)
            val angle = atan2(dy, dx)
            fun wing(side: Int): Pair<Float, Float> {
                val a = angle + Math.PI - side * Math.toRadians(28.0)
                return (x1 + (head * cos(a)).toFloat() / aspect) to (y1 + (head * sin(a)).toFloat())
            }
            listOf(listOf(x0 to y0, x1 to y1), listOf(wing(1), x1 to y1, wing(-1)))
        }
    }

    fun densify(line: List<Pair<Float, Float>>, step: Float): List<Pair<Float, Float>> {
        if (line.size < 2) return line
        val out = ArrayList<Pair<Float, Float>>()
        for (k in 1 until line.size) {
            val (ax, ay) = line[k - 1]
            val (bx, by) = line[k]
            val n = ceil(hypot(bx - ax, by - ay) / step).toInt().coerceAtLeast(1)
            for (i in 0 until n) out += (ax + (bx - ax) * i / n) to (ay + (by - ay) * i / n)
        }
        out += line.last()
        return out
    }
}

data class Shortcut(val label: String, val vk: Int, val mods: Int = 0, val hold: Boolean = false) {
    val keys: String get() = listOfNotNull(
        "Ctrl".takeIf { mods and Shortcuts.CTRL != 0 },
        "Shift".takeIf { mods and Shortcuts.SHIFT != 0 },
        "Alt".takeIf { mods and Shortcuts.ALT != 0 },
        Shortcuts.keyName(vk),
    ).joinToString("+")
}

object Shortcuts {
    const val CTRL = 1
    const val SHIFT = 2
    const val ALT = 4
    const val TAP = 0
    const val PRESS = 1
    const val RELEASE = 2

    val ALL = listOf(
        Shortcut("Undo", 0x5A, CTRL), Shortcut("Redo", 0x59, CTRL), Shortcut("Save", 0x53, CTRL),
        Shortcut("Copy", 0x43, CTRL), Shortcut("Paste", 0x56, CTRL), Shortcut("Cut", 0x58, CTRL),
        Shortcut("All", 0x41, CTRL), Shortcut("Deselect", 0x44, CTRL),
        Shortcut("Pan", 0x20, hold = true), Shortcut("Brush", 0x42), Shortcut("Eraser", 0x45),
        Shortcut("Size −", 0xDB), Shortcut("Size +", 0xDD),
        Shortcut("Zoom +", 0xBB, CTRL), Shortcut("Zoom −", 0xBD, CTRL), Shortcut("Fit", 0x30, CTRL),
        Shortcut("Esc", 0x1B), Shortcut("Enter", 0x0D), Shortcut("Delete", 0x2E), Shortcut("Tab", 0x09),
        Shortcut("Ctrl", 0x11, hold = true), Shortcut("Shift", 0x10, hold = true), Shortcut("Alt", 0x12, hold = true),
    )

    val DEFAULT = listOf("Undo", "Redo", "Pan", "Brush", "Eraser", "Save")

    fun byLabel(label: String) = ALL.firstOrNull { it.label == label }

    fun keyName(vk: Int): String = when (vk) {
        0x20 -> "Space"
        0xDB -> "["
        0xDD -> "]"
        0xBB -> "="
        0xBD -> "−"
        0x1B -> "Esc"
        0x0D -> "Enter"
        0x2E -> "Delete"
        0x09 -> "Tab"
        0x11 -> "Ctrl"
        0x10 -> "Shift"
        0x12 -> "Alt"
        in 0x30..0x39, in 0x41..0x5A -> vk.toChar().toString()
        else -> "0x%02X".format(vk)
    }

    fun press(s: Shortcut, action: Int) = KeyPress(action, s.mods, s.vk)
}
