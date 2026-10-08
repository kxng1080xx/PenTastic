package dev.pentastic

import android.annotation.SuppressLint
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ColorFilter
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.graphics.drawable.StateListDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.PopupWindow
import android.widget.SeekBar
import android.widget.TextView
import kotlin.math.PI
import kotlin.math.roundToInt
import kotlin.math.sin

class Toolbar(
    context: Context,
    private val tools: ToolState,
    private val inks: Array<InkLayer>,
    private val onChange: () -> Unit,
    private val onInk: (command: Int) -> Unit,
) : LinearLayout(context) {
    private val dp = resources.displayMetrics.density
    private fun Number.dp() = (toFloat() * dp).toInt()
    private val buttonSize = resources.displayMetrics.let { m ->
        ((minOf(m.widthPixels, m.heightPixels) - 51.dp()) / 9).coerceIn(30.dp(), 40.dp())
    }
    private val ink get() = inks[tools.mode]

    private val modeButton = button("Mode", R.drawable.ic_mode_control) {
        tools.mode = if (tools.mode == Protocol.MODE_DRAW) Protocol.MODE_CONTROL else Protocol.MODE_DRAW
        changed()
    }
    private val brushButton = button("Brush and shapes") { showBrushes(it) }
    private val eraserButton = button("Eraser", R.drawable.ic_eraser) {
        tools.eraser = !tools.eraser
        changed()
    }
    private val colorButton = button("Color and size") { showColor(it) }
    private val undoButton = button("Undo", R.drawable.ic_undo) { undo() }
    private val redoButton = button("Redo", R.drawable.ic_redo) {
        ink.redo()
        onInk(Protocol.INK_REDO)
        changed()
    }
    private val clearButton = button("Clear (undo brings it back)", R.drawable.ic_clear) {
        ink.clear()
        onInk(Protocol.INK_CLEAR)
        changed()
    }
    private val viewButton = button("View", R.drawable.ic_view) { showView(it) }
    private val settingsButton = button("Pen settings", R.drawable.ic_tune) { showSettings(it) }
    private var popup: PopupWindow? = null

    init {
        orientation = VERTICAL
        background = panel()
        elevation = 6.dp().toFloat()
        setPadding(4.dp(), 4.dp(), 4.dp(), 4.dp())
        isClickable = true
        addView(modeButton)
        addDivider()
        for (b in listOf(brushButton, eraserButton, colorButton)) addView(b)
        addDivider()
        for (b in listOf(undoButton, redoButton, clearButton)) addView(b)
        addDivider()
        for (b in listOf(viewButton, settingsButton)) addView(b)
        refresh()
    }

    fun refresh() {
        val draw = tools.mode == Protocol.MODE_DRAW
        modeButton.setImageResource(if (draw) R.drawable.ic_mode_draw else R.drawable.ic_mode_control)
        modeButton.isSelected = draw
        val modeName = if (draw) "Draw mode: ink goes to the PenTastic canvas. Tap for Control mode."
        else "Control mode: the pen drives PC apps. Tap for Draw mode."
        modeButton.contentDescription = modeName
        modeButton.tooltipText = modeName

        brushButton.setImageDrawable(Preview(tools.brush, tools.shape, tools.inkColor, (tools.sizeDp * dp).coerceAtMost(5.dp().toFloat())))
        brushButton.isSelected = !tools.eraser
        eraserButton.isSelected = tools.eraser
        colorButton.setImageDrawable(SizeDot(tools.inkColor, tools.sizeDp))
        enable(undoButton, ink.canUndo)
        enable(redoButton, ink.canRedo)
        enable(clearButton, !ink.isEmpty)
    }

    fun undo() {
        ink.undo()
        onInk(Protocol.INK_UNDO)
        changed()
    }

    fun dismissPopup() {
        popup?.dismiss()
        popup = null
    }

    private fun changed() {
        tools.save()
        refresh()
        onChange()
    }

    private fun addDivider() = addView(View(context).apply { setBackgroundColor(LINE) }, LayoutParams(28.dp(), 1.dp()).apply {
        gravity = Gravity.CENTER_HORIZONTAL; setMargins(0, 4.dp(), 0, 4.dp())
    })

    private fun showBrushes(anchor: View) {
        val width = (tools.sizeDp * dp).coerceIn(2.dp().toFloat(), 4.dp().toFloat())
        val brushes = listOf(Brush.PEN, Brush.PENCIL, Brush.MARKER).map { b ->
            tile(b.label, Preview(b, Shape.NONE, tools.inkColor, width), b == tools.brush && tools.shape == Shape.NONE && !tools.eraser) {
                tools.brush = b
                tools.shape = Shape.NONE
                tools.eraser = false
            }
        }
        val shapes = listOf(Shape.LINE, Shape.ARROW, Shape.RECT, Shape.ELLIPSE).map { s ->
            tile(s.label, Preview(Brush.PEN, s, tools.inkColor, width), s == tools.shape && !tools.eraser) {
                tools.shape = s
                tools.eraser = false
            }
        }
        showPopup(anchor, row(brushes + separator() + shapes))
    }

    @SuppressLint("SetTextI18n")
    private fun showColor(anchor: View) {
        lateinit var picker: ColorPicker
        lateinit var preview: ImageView
        fun applied() {
            tools.eraser = false
            preview.setImageDrawable(SizeDot(tools.inkColor, tools.sizeDp, big = true))
            changed()
        }
        fun use(c: Int) {
            tools.useColor(c)
            picker.color = c
            applied()
        }
        picker = ColorPicker(context, tools.color) { c ->
            tools.color = c or ToolState.OPAQUE
            applied()
        }
        preview = ImageView(context).apply {
            scaleType = ImageView.ScaleType.CENTER
            setImageDrawable(SizeDot(tools.inkColor, tools.sizeDp, big = true))
        }

        val swatches = row(ToolState.COLORS.mapIndexed { i, c -> swatch(c, ToolState.COLOR_NAMES[i]) { use(c) } }, padding = 0)
        val recent = row(tools.recent.map { c -> swatch(c, "Recent color") { use(c) } }, padding = 0)
        val size = slider("Size", ((tools.sizeDp - ToolState.MIN_SIZE_DP) / (ToolState.MAX_SIZE_DP - ToolState.MIN_SIZE_DP) * 100).roundToInt(),
            { "${"%.1f".format(tools.sizeDp)}" }) { v ->
            tools.sizeDp = ToolState.MIN_SIZE_DP + v / 100f * (ToolState.MAX_SIZE_DP - ToolState.MIN_SIZE_DP)
            applied()
        }
        val opacity = slider("Opacity", ((tools.opacity - ToolState.MIN_OPACITY) / (1 - ToolState.MIN_OPACITY) * 100).roundToInt(),
            { "${(tools.opacity * 100).roundToInt()}%" }) { v ->
            tools.opacity = ToolState.MIN_OPACITY + v / 100f * (1 - ToolState.MIN_OPACITY)
            applied()
        }

        val right = LinearLayout(context).apply {
            orientation = VERTICAL
            setPadding(16.dp(), 0, 0, 0)
            addView(label("Colors"))
            addView(swatches)
            if (tools.recent.isNotEmpty()) {
                addView(label("Recent"))
                addView(recent)
            }
            addView(size)
            addView(opacity)
        }
        val left = LinearLayout(context).apply {
            orientation = VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            addView(picker, LayoutParams(200.dp(), 176.dp()))
            addView(preview, LayoutParams(200.dp(), 48.dp()))
        }
        showPopup(anchor, LinearLayout(context).apply {
            orientation = HORIZONTAL
            setPadding(14.dp(), 12.dp(), 14.dp(), 12.dp())
            addView(left)
            addView(right, LayoutParams(260.dp(), LayoutParams.WRAP_CONTENT))
        })
    }

    private fun swatch(color: Int, name: String, pick: () -> Unit) = View(context).apply {
        background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
            setStroke(1.dp(), LINE)
        }
        contentDescription = name
        tooltipText = name
        layoutParams = LayoutParams(24.dp(), 24.dp()).apply { setMargins(0, 2.dp(), 5.dp(), 2.dp()) }
        setOnClickListener { pick() }
    }

    private fun slider(name: String, value: Int, valueText: () -> String, set: (Int) -> Unit): View {
        val title = label("$name  ${valueText()}")
        val bar = SeekBar(context).apply {
            max = 100
            progress = value
            progressTintList = ColorStateList.valueOf(ACCENT)
            thumbTintList = ColorStateList.valueOf(ACCENT)
            progressBackgroundTintList = ColorStateList.valueOf(LINE)
            contentDescription = name
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(s: SeekBar, v: Int, fromUser: Boolean) {
                    if (!fromUser) return
                    set(v)
                    title.text = "$name  ${valueText()}"
                }
                override fun onStartTrackingTouch(s: SeekBar) {}
                override fun onStopTrackingTouch(s: SeekBar) {}
            })
        }
        return LinearLayout(context).apply {
            orientation = VERTICAL
            addView(title)
            addView(bar, LayoutParams(LayoutParams.MATCH_PARENT, 32.dp()))
        }
    }

    private fun showView(anchor: View) = showPopup(anchor, row(listOf(
        tile("Keep ink", icon(R.drawable.ic_keep), tools.keepInk) { tools.keepInk = !tools.keepInk },
        tile("PC screen", icon(R.drawable.ic_monitor), tools.mirror) { tools.mirror = !tools.mirror },
        tile("Grid", icon(R.drawable.ic_grid), tools.grid) { tools.grid = !tools.grid },
        tile("Keys", icon(R.drawable.ic_keyboard), tools.showKeys) { tools.showKeys = !tools.showKeys },
        separator(),
        tile("White", paperSwatch(ToolState.WHITE_PAPER), tools.paper == Protocol.PAPER_WHITE) { tools.paper = Protocol.PAPER_WHITE },
        tile("Black", paperSwatch(ToolState.BLACK_PAPER), tools.paper == Protocol.PAPER_BLACK) { tools.paper = Protocol.PAPER_BLACK },
    )))

    private fun showSettings(anchor: View) {
        fun <T> choice(title: String, options: List<T>, current: T, name: (T) -> String, set: (T) -> Unit) = LinearLayout(context).apply {
            orientation = VERTICAL
            setPadding(0, 2.dp(), 0, 6.dp())
            addView(label(title))
            addView(LinearLayout(context).apply {
                options.forEach { o ->
                    addView(chip(name(o), o == current) {
                        set(o)
                        tools.save()
                        dismissPopup()
                        showSettings(anchor)
                    })
                }
            })
        }
        showPopup(anchor, LinearLayout(context).apply {
            orientation = VERTICAL
            setPadding(14.dp(), 10.dp(), 14.dp(), 10.dp())
            addView(choice("Smoothing: steadier lines", Smoothing.entries, tools.smoothing, { it.label }) { tools.smoothing = it })
            addView(choice("Pressure feel", PressureFeel.entries, tools.feel, { it.label }) { tools.feel = it })
            addView(choice("S Pen button", PenButton.entries, tools.penButton, { it.label }) { tools.penButton = it })
        })
    }

    private fun chip(text: String, selected: Boolean, pick: () -> Unit) = TextView(context).apply {
        this.text = text
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
        setTextColor(if (selected) ACCENT_TEXT else TEXT)
        background = GradientDrawable().apply {
            cornerRadius = 8.dp().toFloat()
            setColor(if (selected) ACCENT else SELECTED)
        }
        setPadding(12.dp(), 7.dp(), 12.dp(), 7.dp())
        isSelected = selected
        layoutParams = LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT).apply { setMargins(0, 0, 6.dp(), 0) }
        setOnClickListener { pick() }
    }

    private fun tile(label: String, image: Drawable, selected: Boolean, choose: () -> Unit) = LinearLayout(context).apply {
        orientation = VERTICAL
        gravity = Gravity.CENTER_HORIZONTAL
        setPadding(4.dp(), 6.dp(), 4.dp(), 4.dp())
        background = itemBackground()
        isSelected = selected
        contentDescription = label
        addView(ImageView(context).apply {
            setImageDrawable(image)
            scaleType = ImageView.ScaleType.CENTER
            isDuplicateParentStateEnabled = true
        }, LayoutParams(56.dp(), 28.dp()))
        addView(TextView(context).apply {
            text = label
            setTextColor(TEXT)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
        })
        setOnClickListener {
            choose()
            dismissPopup()
            changed()
        }
    }

    private fun label(text: String) = TextView(context).apply {
        this.text = text
        setTextColor(MUTED)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
        setPadding(0, 6.dp(), 0, 4.dp())
    }

    private fun row(items: List<View>, padding: Int = 6) = LinearLayout(context).apply {
        orientation = HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        setPadding(padding.dp(), padding.dp(), padding.dp(), padding.dp())
        items.forEach { addView(it) }
    }

    private fun separator() = View(context).apply {
        setBackgroundColor(LINE)
        layoutParams = LayoutParams(1.dp(), 40.dp()).apply { setMargins(6.dp(), 0, 6.dp(), 0) }
    }

    private fun icon(res: Int): Drawable = context.getDrawable(res)!!.mutate().apply {
        setTintList(ColorStateList(arrayOf(intArrayOf(android.R.attr.state_selected), intArrayOf()), intArrayOf(ACCENT, TEXT)))
    }

    private fun paperSwatch(color: Int) = GradientDrawable().apply {
        setColor(color)
        cornerRadius = 4.dp().toFloat()
        setStroke(1.dp(), LINE)
        setSize(30.dp(), 20.dp())
    }

    private fun showPopup(anchor: View, content: View) {
        dismissPopup()
        content.measure(MeasureSpec.UNSPECIFIED, MeasureSpec.UNSPECIFIED)
        popup = PopupWindow(content, LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT, false).apply {
            setBackgroundDrawable(panel())
            elevation = 8.dp().toFloat()
            isOutsideTouchable = true
            setOnDismissListener { if (popup === this) popup = null }
        }
        val rail = IntArray(2).also { getLocationOnScreen(it) }
        val at = IntArray(2).also { anchor.getLocationOnScreen(it) }
        val screen = rootView.height
        val y = (at[1] + anchor.height / 2 - content.measuredHeight / 2)
            .coerceAtMost(screen - content.measuredHeight - 8.dp()).coerceAtLeast(8.dp())
        popup!!.showAtLocation(this, Gravity.TOP or Gravity.START, rail[0] + width + 8.dp(), y)
    }

    private fun button(name: String, icon: Int = 0, onClick: (View) -> Unit) = ImageView(context).apply {
        if (icon != 0) {
            setImageResource(icon)
            imageTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_selected), intArrayOf()), intArrayOf(ACCENT, TEXT),
            )
        }
        scaleType = ImageView.ScaleType.CENTER
        background = itemBackground()
        contentDescription = name
        tooltipText = name
        layoutParams = LayoutParams(buttonSize, buttonSize)
        setOnClickListener(onClick)
    }

    private fun enable(v: View, on: Boolean) {
        v.isEnabled = on
        v.alpha = if (on) 1f else 0.35f
    }

    private fun panel() = GradientDrawable().apply {
        setColor(PANEL)
        cornerRadius = 16.dp().toFloat()
        setStroke(1.dp(), LINE)
    }

    private fun itemBackground(): Drawable {
        val shape = { color: Int -> GradientDrawable().apply { setColor(color); cornerRadius = 12.dp().toFloat() } }
        val content = StateListDrawable().apply {
            addState(intArrayOf(android.R.attr.state_selected), shape(SELECTED))
        }
        return RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), content, shape(0xFFFFFFFF.toInt()))
    }

    private inner class Preview(brush: Brush, shape: Shape, color: Int, width: Float) : Drawable() {
        private val w = width.coerceAtMost(6.dp() / if (brush == Brush.MARKER) 3f else 1f)
        private val strokes = if (shape == Shape.NONE) listOf(Stroke(brush, color, w).apply {
            for (i in 0..24) {
                val t = i / 24f
                add(0.15f + 0.7f * t, 0.5f - 0.22f * sin(t * 2 * PI).toFloat(), 0.25f + 0.75f * sin(t * PI).toFloat())
            }
        }) else PenTools.shape(shape, 0.2f, 0.25f, 0.8f, 0.75f, 30f / 22).map { line ->
            Stroke(brush, color, w).apply { PenTools.densify(line, 0.05f).forEach { (x, y) -> add(x, y, 0.7f) } }
        }

        override fun draw(c: Canvas) {
            c.save()
            c.translate(bounds.left.toFloat(), bounds.top.toFloat())
            for (s in strokes) StrokePainter.paint(c, s, bounds.width().toFloat(), bounds.height().toFloat())
            c.restore()
        }

        override fun getIntrinsicWidth() = 30.dp()
        override fun getIntrinsicHeight() = 22.dp()
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Deprecated("Deprecated in Java")
        override fun getOpacity() = PixelFormat.TRANSLUCENT
    }

    private inner class SizeDot(private val color: Int, sizeDp: Float, big: Boolean = false) : Drawable() {
        private val box = if (big) 40.dp() else 26.dp()
        private val radius = (4.dp() + (box / 2 - 4.dp()) * (sizeDp - ToolState.MIN_SIZE_DP) / (ToolState.MAX_SIZE_DP - ToolState.MIN_SIZE_DP))
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val oval = RectF()

        override fun draw(c: Canvas) {
            val cx = bounds.exactCenterX()
            val cy = bounds.exactCenterY()
            oval.set(cx - radius, cy - radius, cx + radius, cy + radius)
            paint.style = Paint.Style.FILL
            paint.color = 0xFF3A4A68.toInt()
            c.drawCircle(cx, cy, radius, paint)
            paint.color = 0xFF6F86AD.toInt()
            c.drawArc(oval, 0f, 90f, true, paint)
            c.drawArc(oval, 180f, 90f, true, paint)
            paint.color = color
            c.drawCircle(cx, cy, radius, paint)
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = 1.dp().toFloat()
            paint.color = LINE
            c.drawCircle(cx, cy, radius, paint)
        }

        override fun getIntrinsicWidth() = box
        override fun getIntrinsicHeight() = box
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Deprecated("Deprecated in Java")
        override fun getOpacity() = PixelFormat.TRANSLUCENT
    }

    @SuppressLint("ViewConstructor")
    private class ColorPicker(context: Context, initial: Int, private val onPick: (Int) -> Unit) : View(context) {
        private val dp = resources.displayMetrics.density
        private val hsv = FloatArray(3).also { Color.colorToHSV(initial, it) }
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val barHeight = 18 * dp
        private val gap = 12 * dp
        private var dragging = 0

        var color: Int
            get() = Color.HSVToColor(hsv)
            set(value) {
                Color.colorToHSV(value, hsv)
                invalidate()
            }

        init {
            contentDescription = "Color picker"
        }

        private val square = RectF()
        private val bar = RectF()
        private var saturation: Shader? = null
        private var saturationHue = -1f
        private var brightness: Shader? = null
        private var hues: Shader? = null

        override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
            square.set(0f, 0f, w.toFloat(), h - barHeight - gap)
            bar.set(0f, h - barHeight, w.toFloat(), h.toFloat())
            brightness = LinearGradient(0f, square.top, 0f, square.bottom, Color.TRANSPARENT, Color.BLACK, Shader.TileMode.CLAMP)
            hues = LinearGradient(bar.left, 0f, bar.right, 0f, IntArray(7) { Color.HSVToColor(floatArrayOf(it * 60f, 1f, 1f)) }, null, Shader.TileMode.CLAMP)
            saturationHue = -1f
        }

        override fun onDraw(c: Canvas) {
            val sq = square
            val r = 8 * dp
            if (saturationHue != hsv[0]) {
                saturation = LinearGradient(sq.left, 0f, sq.right, 0f, Color.WHITE, Color.HSVToColor(floatArrayOf(hsv[0], 1f, 1f)), Shader.TileMode.CLAMP)
                saturationHue = hsv[0]
            }
            paint.style = Paint.Style.FILL
            paint.shader = saturation
            c.drawRoundRect(sq, r, r, paint)
            paint.shader = brightness
            c.drawRoundRect(sq, r, r, paint)
            val b = bar
            paint.shader = hues
            c.drawRoundRect(b, barHeight / 2, barHeight / 2, paint)
            paint.shader = null

            ring(c, sq.left + hsv[1] * sq.width(), sq.top + (1 - hsv[2]) * sq.height())
            ring(c, b.left + hsv[0] / 360f * b.width(), b.centerY())
        }

        private fun ring(c: Canvas, x: Float, y: Float) {
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = 3 * dp
            paint.color = Color.WHITE
            c.drawCircle(x, y, 8 * dp, paint)
            paint.strokeWidth = 1 * dp
            paint.color = 0x99000000.toInt()
            c.drawCircle(x, y, 9.5f * dp, paint)
        }

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            if (e.actionMasked == MotionEvent.ACTION_DOWN) dragging = if (e.y < square.bottom + gap / 2) 1 else 2
            if (dragging == 1) {
                val sq = square
                hsv[1] = ((e.x - sq.left) / sq.width()).coerceIn(0f, 1f)
                hsv[2] = 1 - ((e.y - sq.top) / sq.height()).coerceIn(0f, 1f)
            } else hsv[0] = ((e.x - bar.left) / bar.width() * 360f).coerceIn(0f, 359.9f)
            invalidate()
            onPick(color)
            if (e.actionMasked == MotionEvent.ACTION_UP) dragging = 0
            return true
        }
    }

    companion object {
        const val PANEL = 0xF01A2436.toInt()
        const val LINE = 0xFF2C3D5C.toInt()
        const val SELECTED = 0xFF2F4466.toInt()
        const val TEXT = 0xFFC9D4E6.toInt()
        const val MUTED = 0xFF8A9BB8.toInt()
        const val ACCENT = 0xFFFFD166.toInt()
        const val ACCENT_TEXT = 0xFF1A1405.toInt()
    }
}
