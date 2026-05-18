package com.mtschoen.windowstream.viewer.demo

import android.content.Context
import android.graphics.Color
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.mtschoen.windowstream.viewer.observability.LogEvent
import com.mtschoen.windowstream.viewer.observability.Severity
import com.mtschoen.windowstream.viewer.observability.StageStatus
import com.mtschoen.windowstream.viewer.observability.ViewerState

class ObservabilityOverlay(context: Context) {

    private val statusLines: LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(24, 24, 24, 24)
    }
    private val eventLogContainer: LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(24, 0, 24, 24)
    }
    private val eventLogScroll: ScrollView = ScrollView(context).apply {
        addView(eventLogContainer)
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f
        )
    }
    val rootView: FrameLayout = FrameLayout(context).apply {
        setBackgroundColor(Color.argb(220, 0, 0, 0))
        visibility = View.GONE
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
            addView(statusLines)
            addView(eventLogScroll)
        })
    }

    fun show() { rootView.visibility = View.VISIBLE }
    fun hide() { rootView.visibility = View.GONE }
    fun toggle() { if (rootView.visibility == View.VISIBLE) hide() else show() }

    fun renderState(state: ViewerState) {
        statusLines.removeAllViews()
        addLine(state.discovery, "Discovery", state.discoveredServer ?: "")
        addLine(state.tcpConnect, "TCP connect", state.tcpConnectError ?: "")
        addLine(state.serverHello, "ServerHello", "${state.windowCount} window(s)")
        state.streams.forEach { (streamId, row) ->
            addLine(StageStatus.Ok, "Stream #$streamId", "")
            addLine(row.openStream, "  open", row.openStreamError ?: "")
            addLine(row.udpArriving, "  UDP", row.udpFirstDelayMs?.let { "first packet ${it}ms" } ?: "")
            addLine(row.decoder, "  decoder", row.decoderError ?: "")
            addLine(row.presenting, "  presenting", row.fps?.let { "%.1f fps".format(it) } ?: "")
        }
    }

    fun appendEvent(event: LogEvent) {
        val line = TextView(rootView.context).apply {
            textSize = 11f
            text = "%s %s %s %s".format(
                event.timestamp.toString().substringAfterLast(":").take(8),
                event.severity.name.take(1),
                event.eventType,
                event.message,
            )
            setTextColor(when (event.severity) {
                Severity.ERROR -> Color.rgb(255, 100, 100)
                Severity.WARNING -> Color.rgb(255, 200, 80)
                else -> Color.rgb(200, 200, 200)
            })
        }
        eventLogContainer.addView(line)
        while (eventLogContainer.childCount > 200) eventLogContainer.removeViewAt(0)
        eventLogScroll.post { eventLogScroll.fullScroll(View.FOCUS_DOWN) }
    }

    private fun addLine(status: StageStatus, label: String, detail: String) {
        val glyph = when (status) {
            StageStatus.Ok -> "✓"
            StageStatus.Warning -> "⚠"
            StageStatus.Error -> "✗"
            StageStatus.InProgress -> "…"
            else -> "—"
        }
        statusLines.addView(TextView(rootView.context).apply {
            text = "$glyph  $label  $detail"
            setTextColor(when (status) {
                StageStatus.Error -> Color.rgb(255, 100, 100)
                StageStatus.Warning -> Color.rgb(255, 200, 80)
                else -> Color.WHITE
            })
            textSize = 14f
        })
    }
}
