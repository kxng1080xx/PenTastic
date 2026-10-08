package dev.pentastic

import android.annotation.SuppressLint
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.graphics.drawable.StateListDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.PopupWindow
import android.widget.TextView

@SuppressLint("ViewConstructor", "ClickableViewAccessibility")
class KeyStrip(
    context: Context,
    private val tools: ToolState,
    private val send: (KeyPress) -> Unit,
) : LinearLayout(context) {

    private val dp = resources.displayMetrics.density
    private fun Number.dp() = (toFloat() * dp).toInt()
    private val keyHeight = resources.displayMetrics.let { m ->
        ((minOf(m.widthPixels, m.heightPixels) - 40.dp()) / (Shortcuts.DEFAULT.size + 1)).coerceIn(30.dp(), 44.dp())
    }
    private var editing = false
    private var popup: PopupWindow? = null
    private val held = HashSet<Shortcut>()

    init {
        orientation = VERTICAL
        background = GradientDrawable().apply {
            setColor(Toolbar.PANEL)
            cornerRadius = 16.dp().toFloat()
            setStroke(1.dp(), Toolbar.LINE)
        }
        elevation = 6.dp().toFloat()
        setPadding(4.dp(), 4.dp(), 4.dp(), 4.dp())
        isClickable = true
        rebuild()
    }

    fun releaseAll() {
        for (s in held) send(Shortcuts.press(s, Shortcuts.RELEASE))
        held.clear()
        dismissPopup()
    }

    fun dismissPopup() {
        popup?.dismiss()
        popup = null
    }

    private fun rebuild() {
        removeAllViews()
        tools.keys.forEachIndexed { slot, s -> addView(key(slot, s)) }
        addView(iconKey(if (editing) R.drawable.ic_key_done else R.drawable.ic_key_edit, accent = editing).apply {
            contentDescription = if (editing) "Finish editing keys" else "Edit keys"
            setOnClickListener {
                editing = !editing
                dismissPopup()
                rebuild()
            }
        })
    }

    private fun key(slot: Int, s: Shortcut) = (ICONS[s.label]?.let { iconKey(it, accent = false) } ?: label(s.label)).apply {
        contentDescription = "${s.label}, ${s.keys}" + if (s.hold) ", hold" else ""
        tooltipText = s.keys
        if (editing) background = GradientDrawable().apply {
            cornerRadius = 10.dp().toFloat()
            setStroke(1.dp(), Toolbar.ACCENT, 4.dp().toFloat(), 3.dp().toFloat())
        }
        setOnTouchListener { v, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    v.isPressed = true
                    if (editing) choose(slot, v)
                    else {
                        v.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        if (s.hold && held.add(s)) {
                            send(Shortcuts.press(s, Shortcuts.PRESS))
                            v.isSelected = true
                        }
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    v.isPressed = false
                    if (!editing) {
                        if (s.hold) {
                            if (held.remove(s)) send(Shortcuts.press(s, Shortcuts.RELEASE))
                            v.isSelected = false
                        } else if (e.actionMasked == MotionEvent.ACTION_UP) send(Shortcuts.press(s, Shortcuts.TAP))
                    }
                }
            }
            true
        }
    }

    private fun choose(slot: Int, anchor: View) {
        dismissPopup()
        val grid = LinearLayout(context).apply {
            orientation = VERTICAL
            setPadding(10.dp(), 10.dp(), 10.dp(), 10.dp())
            addView(TextView(context).apply {
                text = "Choose a key"
                setTextColor(Toolbar.MUTED)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
                setPadding(2.dp(), 0, 0, 6.dp())
            })
            Shortcuts.ALL.chunked(6).forEach { row ->
                addView(LinearLayout(context).apply {
                    row.forEach { s ->
                        addView(chip(s, s == tools.keys[slot]) {
                            tools.keys[slot] = s
                            tools.save()
                            dismissPopup()
                            rebuild()
                        })
                    }
                })
            }
            measure(MeasureSpec.UNSPECIFIED, MeasureSpec.UNSPECIFIED)
        }
        popup = PopupWindow(grid, LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT, false).apply {
            setBackgroundDrawable(GradientDrawable().apply {
                setColor(Toolbar.PANEL)
                cornerRadius = 16.dp().toFloat()
                setStroke(1.dp(), Toolbar.LINE)
            })
            elevation = 8.dp().toFloat()
            isOutsideTouchable = true
            setOnDismissListener { if (popup === this) popup = null }
        }
        val strip = IntArray(2).also { getLocationOnScreen(it) }
        val at = IntArray(2).also { anchor.getLocationOnScreen(it) }
        val y = (at[1] + anchor.height / 2 - grid.measuredHeight / 2)
            .coerceAtMost(rootView.height - grid.measuredHeight - 8.dp()).coerceAtLeast(8.dp())
        popup!!.showAtLocation(this, Gravity.TOP or Gravity.START, strip[0] - grid.measuredWidth - 8.dp(), y)
    }

    private fun chip(s: Shortcut, selected: Boolean, pick: () -> Unit) = LinearLayout(context).apply {
        orientation = VERTICAL
        gravity = Gravity.CENTER
        setPadding(6.dp(), 5.dp(), 6.dp(), 5.dp())
        background = GradientDrawable().apply {
            cornerRadius = 8.dp().toFloat()
            setColor(if (selected) Toolbar.ACCENT else Toolbar.SELECTED)
        }
        contentDescription = "${s.label}, ${s.keys}"
        val fg = if (selected) Toolbar.ACCENT_TEXT else Toolbar.TEXT
        val icon = ICONS[s.label]
        if (icon != null) addView(ImageView(context).apply {
            setImageResource(icon)
            imageTintList = ColorStateList.valueOf(fg)
            layoutParams = LayoutParams(20.dp(), 20.dp())
        })
        else addView(TextView(context).apply {
            text = s.label
            setTextColor(fg)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
        })
        addView(TextView(context).apply {
            text = (if (icon != null) s.label else s.keys) + if (s.hold) " (hold)" else ""
            setTextColor(if (selected) Toolbar.ACCENT_TEXT else Toolbar.MUTED)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 10f)
        })
        layoutParams = LayoutParams(78.dp(), LayoutParams.WRAP_CONTENT).apply { setMargins(0, 0, 5.dp(), 5.dp()) }
        setOnClickListener { pick() }
    }

    private fun tint(accent: Boolean) = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_selected), intArrayOf()),
        intArrayOf(Toolbar.ACCENT, if (accent) Toolbar.ACCENT else Toolbar.TEXT))

    private fun label(text: String) = TextView(context).apply {
        this.text = text
        gravity = Gravity.CENTER
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
        setTextColor(tint(accent = false))
        background = itemBackground()
        layoutParams = LayoutParams(keyHeight, keyHeight)
    }

    private fun iconKey(icon: Int, accent: Boolean) = ImageView(context).apply {
        setImageResource(icon)
        imageTintList = tint(accent)
        scaleType = ImageView.ScaleType.CENTER
        background = itemBackground()
        layoutParams = LayoutParams(keyHeight, keyHeight)
    }

    private companion object {
        val ICONS = mapOf(
            "Undo" to R.drawable.ic_undo, "Redo" to R.drawable.ic_redo, "Save" to R.drawable.ic_key_save,
            "Copy" to R.drawable.ic_key_copy, "Paste" to R.drawable.ic_key_paste, "Cut" to R.drawable.ic_key_cut,
            "All" to R.drawable.ic_key_select_all, "Deselect" to R.drawable.ic_key_deselect,
            "Pan" to R.drawable.ic_key_pan, "Brush" to R.drawable.ic_mode_draw, "Eraser" to R.drawable.ic_eraser,
            "Size −" to R.drawable.ic_key_size_down, "Size +" to R.drawable.ic_key_size_up,
            "Zoom +" to R.drawable.ic_key_zoom_in, "Zoom −" to R.drawable.ic_key_zoom_out, "Fit" to R.drawable.ic_key_fit,
            "Enter" to R.drawable.ic_key_enter, "Delete" to R.drawable.ic_clear, "Tab" to R.drawable.ic_key_tab,
            "Shift" to R.drawable.ic_key_shift,
        )
    }

    private fun itemBackground(): Drawable {
        val shape = { color: Int -> GradientDrawable().apply { setColor(color); cornerRadius = 10.dp().toFloat() } }
        val content = StateListDrawable().apply {
            addState(intArrayOf(android.R.attr.state_selected), shape(Toolbar.SELECTED))
            addState(intArrayOf(android.R.attr.state_pressed), shape(Toolbar.SELECTED))
        }
        return RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), content, shape(0xFFFFFFFF.toInt()))
    }
}
