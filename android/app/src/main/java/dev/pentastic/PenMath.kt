package dev.pentastic

import kotlin.math.PI
import kotlin.math.atan
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.tan

object PenMath {
    data class Box(val left: Float, val top: Float, val width: Float, val height: Float)

    fun letterbox(viewW: Float, viewH: Float, targetW: Int, targetH: Int, margin: Float): Box {
        val scale = min((viewW - 2 * margin) / targetW, (viewH - 2 * margin) / targetH)
        val w = targetW * scale
        val h = targetH * scale
        return Box((viewW - w) / 2, (viewH - h) / 2, w, h)
    }

    data class Zoom(val x: Float = 0f, val y: Float = 0f, val scale: Float = 1f) {
        fun targetX(padX: Float) = x + padX.coerceIn(0f, 1f) / scale
        fun targetY(padY: Float) = y + padY.coerceIn(0f, 1f) / scale
    }

    const val MAX_ZOOM = 8f

    fun pinch(from: Zoom, fromX: Float, fromY: Float, fromSpan: Float, toX: Float, toY: Float, toSpan: Float): Zoom {
        val s = (from.scale * toSpan / fromSpan).coerceIn(1f, MAX_ZOOM)
        val max = 1 - 1 / s
        return Zoom((from.targetX(fromX) - toX / s).coerceIn(0f, max), (from.targetY(fromY) - toY / s).coerceIn(0f, max), s)
    }

    fun unit(v: Float): Int = (v.coerceIn(0f, 1f) * 65535).roundToInt()

    fun tilt(tilt: Float, orientation: Float): Pair<Int, Int> {
        val t = tan(tilt.toDouble().coerceIn(0.0, PI / 2))
        val x = Math.toDegrees(atan(t * sin(orientation.toDouble())))
        val y = Math.toDegrees(atan(-t * cos(orientation.toDouble())))
        return x.roundToInt() to y.roundToInt()
    }
}
