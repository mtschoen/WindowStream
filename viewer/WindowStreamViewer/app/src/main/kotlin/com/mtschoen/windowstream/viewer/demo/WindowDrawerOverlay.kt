package com.mtschoen.windowstream.viewer.demo

import android.animation.ObjectAnimator
import android.content.Context
import android.graphics.Color
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.mtschoen.windowstream.viewer.control.WindowDescriptor

/**
 * Sliding drawer overlay that shows the live window catalogue from the server.
 * Windows currently being streamed are highlighted. Tapping a window toggles its
 * open/close state — open windows get a CLOSE_STREAM, closed windows get an OPEN_STREAM.
 *
 * The drawer slides in from the left edge of the screen when [show] is called and
 * slides out when [hide] is called. [toggle] alternates between the two states.
 *
 * All window-catalogue mutations (add/remove/update) are applied via [updateCatalogue],
 * which is designed to be called from [WindowPickerViewModel]-style live push events.
 */
class WindowDrawerOverlay(
    context: Context,
    private val onWindowToggled: (windowId: ULong, isCurrentlyOpen: Boolean) -> Unit
) {

    /** The root view to add to the activity's layout. */
    val rootView: FrameLayout

    private val drawerPanel: LinearLayout
    private val windowListContainer: LinearLayout
    private val scrim: View
    private var isVisible: Boolean = false

    // Snapshot of current catalogue and open-stream state.
    private var catalogue: Map<ULong, WindowDescriptor> = emptyMap()
    private var openWindowIds: Set<ULong> = emptySet()

    init {
        // Semi-transparent scrim behind the drawer.
        scrim = View(context).apply {
            setBackgroundColor(Color.argb(120, 0, 0, 0))
            visibility = View.GONE
            isClickable = true
            setOnClickListener { hide() }
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
        }

        // The drawer panel itself.
        windowListContainer = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, 16, 0, 16)
        }

        val scrollView = ScrollView(context).apply {
            addView(windowListContainer, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ))
        }

        val headerLabel = TextView(context).apply {
            text = "Available Windows"
            setTextColor(Color.WHITE)
            textSize = 18f
            setPadding(32, 24, 32, 16)
        }

        drawerPanel = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.rgb(25, 25, 35))
            addView(headerLabel, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ))
            addView(scrollView, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0, 1f
            ))
            layoutParams = FrameLayout.LayoutParams(
                DRAWER_WIDTH_PX,
                FrameLayout.LayoutParams.MATCH_PARENT,
                Gravity.START
            )
            translationX = -DRAWER_WIDTH_PX.toFloat()
            elevation = 8f
        }

        rootView = FrameLayout(context).apply {
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
            addView(scrim)
            addView(drawerPanel)
        }
    }

    fun show() {
        if (isVisible) return
        isVisible = true
        scrim.visibility = View.VISIBLE
        ObjectAnimator.ofFloat(drawerPanel, "translationX", -DRAWER_WIDTH_PX.toFloat(), 0f)
            .apply { duration = ANIMATION_DURATION_MILLISECONDS; start() }
    }

    fun hide() {
        if (!isVisible) return
        isVisible = false
        ObjectAnimator.ofFloat(drawerPanel, "translationX", 0f, -DRAWER_WIDTH_PX.toFloat())
            .apply {
                duration = ANIMATION_DURATION_MILLISECONDS
                addListener(object : android.animation.AnimatorListenerAdapter() {
                    override fun onAnimationEnd(animation: android.animation.Animator) {
                        scrim.visibility = View.GONE
                    }
                })
                start()
            }
    }

    fun toggle() {
        if (isVisible) hide() else show()
    }

    /**
     * Replaces the displayed window catalogue and marks [openIds] as currently streaming.
     * Call this from the main thread.
     */
    fun updateCatalogue(windows: Map<ULong, WindowDescriptor>, openIds: Set<ULong>) {
        catalogue = windows
        openWindowIds = openIds
        rebuildWindowList()
    }

    /** Updates only the set of open window IDs without changing the catalogue. */
    fun updateOpenWindows(openIds: Set<ULong>) {
        openWindowIds = openIds
        rebuildWindowList()
    }

    private fun rebuildWindowList() {
        windowListContainer.removeAllViews()
        val sortedWindows = catalogue.values
            .sortedWith(compareBy({ it.processName }, { it.title }))

        if (sortedWindows.isEmpty()) {
            val emptyLabel = TextView(windowListContainer.context).apply {
                text = "No windows available"
                setTextColor(Color.GRAY)
                textSize = 15f
                setPadding(32, 24, 32, 24)
            }
            windowListContainer.addView(emptyLabel)
            return
        }

        for (window in sortedWindows) {
            val isOpen = window.windowId in openWindowIds
            val row = createWindowRow(windowListContainer.context, window, isOpen)
            windowListContainer.addView(row)
        }
    }

    private fun createWindowRow(context: Context, window: WindowDescriptor, isOpen: Boolean): View {
        val titleLabel = TextView(context).apply {
            text = window.title.ifBlank { "(untitled)" }
            setTextColor(Color.WHITE)
            textSize = 16f
            maxLines = 1
        }

        val detailLabel = TextView(context).apply {
            text = "${window.processName}  ${window.physicalWidth}×${window.physicalHeight}"
            setTextColor(Color.rgb(160, 160, 170))
            textSize = 12f
            maxLines = 1
        }

        val statusIndicator = View(context).apply {
            setBackgroundColor(if (isOpen) Color.rgb(80, 200, 80) else Color.TRANSPARENT)
            layoutParams = LinearLayout.LayoutParams(6, LinearLayout.LayoutParams.MATCH_PARENT).apply {
                setMargins(0, 4, 16, 4)
            }
        }

        val textColumn = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            addView(titleLabel, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ))
            addView(detailLabel, LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT
            ))
        }

        return LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(20, 16, 32, 16)
            setBackgroundColor(if (isOpen) Color.rgb(35, 45, 55) else Color.TRANSPARENT)
            isClickable = true
            isFocusable = true
            addView(statusIndicator)
            addView(textColumn, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            setOnClickListener {
                Log.i(TAG, "window tapped: windowId=${window.windowId} title=${window.title} isCurrentlyOpen=$isOpen")
                onWindowToggled(window.windowId, isOpen)
            }
        }
    }

    private companion object {
        const val TAG = "WindowDrawer"
        const val DRAWER_WIDTH_PX = 600
        const val ANIMATION_DURATION_MILLISECONDS: Long = 250
    }
}
