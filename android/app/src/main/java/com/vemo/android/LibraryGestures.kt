package com.vemo.android

import android.annotation.SuppressLint
import android.content.ClipData
import android.content.Context
import android.view.DragEvent
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.widget.ScrollView
import kotlin.math.abs

/** Only passed as localState. No URI, global drag flag, or cross-app payload. */
internal data class LibraryDrag(val id: String, val folder: Boolean)

/** Touch long hold arms a drag; release without moving opens management instead. */
@SuppressLint("ClickableViewAccessibility") // Tap calls performClick; accessibility long click opens the menu directly.
internal class LibraryHold(
    private val view: View,
    private val item: LibraryDrag,
    private val freeze: (Boolean) -> Unit,
    private val menu: () -> Unit
) {
    private val slop = ViewConfiguration.get(view.context).scaledTouchSlop
    private var downX = 0f
    private var downY = 0f
    private var tracking = false
    private var frozen = false
    private var armed = false
    private var dragging = false
    private val arm = Runnable {
        if (tracking && view.isAttachedToWindow) {
            armed = true
            view.isPressed = true
            view.parent?.requestDisallowInterceptTouchEvent(true)
            view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
        }
    }

    init {
        view.setOnLongClickListener { menu(); true }
        view.setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = event.rawX; downY = event.rawY
                    tracking = true; dragging = false
                    frozen = true; freeze(true)
                    view.isPressed = true
                    view.postDelayed(arm, ViewConfiguration.getLongPressTimeout().toLong())
                }
                MotionEvent.ACTION_MOVE -> {
                    val moved = abs(event.rawX - downX) > slop || abs(event.rawY - downY) > slop
                    if (moved && armed && !dragging) {
                        // Mark before startDragAndDrop: it may synchronously deliver touch CANCEL.
                        dragging = true; tracking = false; view.isPressed = false
                        val started = view.startDragAndDrop(
                            ClipData.newPlainText("Vemo library item", ""), View.DragShadowBuilder(view), item, 0)
                        if (!started) { dragging = false; cancel() }
                    } else if (moved && !armed) {
                        cancel()
                        // The ScrollView remains free to intercept an ordinary scroll before hold.
                    }
                }
                MotionEvent.ACTION_UP -> {
                    val openMenu = armed && !dragging
                    val tap = tracking && !armed && !dragging
                    if (!dragging) cancel()
                    if (openMenu) menu() else if (tap) view.performClick()
                }
                MotionEvent.ACTION_CANCEL -> if (!dragging) cancel()
                MotionEvent.ACTION_POINTER_DOWN -> if (!dragging) cancel()
            }
            true
        }
    }

    fun belongsTo(parent: View) = view.parent === parent

    fun cancel() {
        view.removeCallbacks(arm)
        val wasFrozen = frozen
        frozen = false
        armed = false; tracking = false; dragging = false; view.isPressed = false
        view.parent?.requestDisallowInterceptTouchEvent(false)
        if (wasFrozen) freeze(false)
    }
}

/** Observes dispatch so edge scrolling also works when a child handles the drop. */
internal class LibraryDragScroll(context: Context, private val ended: () -> Unit) : ScrollView(context) {
    private var speed = 0
    private var active = false
    private val step = object : Runnable {
        override fun run() {
            if (!active || speed == 0) return
            scrollBy(0, speed)
            postOnAnimation(this)
        }
    }

    init {
        // Keep receiving local drag events over whitespace without accepting a drop there.
        setOnDragListener { _, event -> event.localState is LibraryDrag && event.action != DragEvent.ACTION_DROP }
    }

    override fun dispatchDragEvent(event: DragEvent): Boolean {
        if (event.localState is LibraryDrag) {
            when (event.action) {
                DragEvent.ACTION_DRAG_STARTED -> active = true
                DragEvent.ACTION_DRAG_LOCATION -> {
                    val edge = (48 * resources.displayMetrics.density).toInt().coerceAtMost(height / 3)
                    val delta = (8 * resources.displayMetrics.density).toInt().coerceAtLeast(1)
                    val next = when {
                        event.y < edge -> -delta
                        event.y > height - edge -> delta
                        else -> 0
                    }
                    if (next != speed) { removeCallbacks(step); speed = next; if (speed != 0) postOnAnimation(step) }
                }
                DragEvent.ACTION_DRAG_EXITED, DragEvent.ACTION_DROP -> stopScrolling()
                DragEvent.ACTION_DRAG_ENDED -> { active = false; stopScrolling(); ended() }
            }
        }
        val handled = super.dispatchDragEvent(event)
        // Stay registered for ENDED even when every child rejects a self/no-op move.
        // DROP still returns the actual dispatch result, so whitespace/invalid targets fail.
        return if (event.localState is LibraryDrag && event.action == DragEvent.ACTION_DRAG_STARTED) true else handled
    }

    private fun stopScrolling() { speed = 0; removeCallbacks(step) }
    override fun onDetachedFromWindow() { active = false; stopScrolling(); super.onDetachedFromWindow() }
}
