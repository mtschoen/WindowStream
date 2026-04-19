package com.mtschoen.windowstream.viewer.state

import com.mtschoen.windowstream.viewer.discovery.ServerInformation

sealed class ViewerState {
    data object Disconnected : ViewerState()
    data object Discovering : ViewerState()
    data class Connected(val server: ServerInformation) : ViewerState()
    data class Streaming(
        val server: ServerInformation,
        val streamId: Int,
        val width: Int,
        val height: Int
    ) : ViewerState()
    data class Error(val code: String, val message: String) : ViewerState()
}

sealed class ViewerEvent {
    data object StartDiscovery : ViewerEvent()
    data class ServerSelected(val server: ServerInformation) : ViewerEvent()
    data class StreamStarted(val streamId: Int, val width: Int, val height: Int) : ViewerEvent()
    data class StreamStopped(val streamId: Int) : ViewerEvent()
    data class FatalError(val code: String, val message: String) : ViewerEvent()
    data object Disconnect : ViewerEvent()
}

class ViewerStateMachine {
    var currentState: ViewerState = ViewerState.Disconnected
        private set

    fun reduce(event: ViewerEvent) {
        val previousState: ViewerState = currentState
        currentState = when (event) {
            is ViewerEvent.StartDiscovery -> when (previousState) {
                ViewerState.Disconnected -> ViewerState.Discovering
                else -> previousState
            }
            is ViewerEvent.ServerSelected -> when (previousState) {
                ViewerState.Discovering -> ViewerState.Connected(event.server)
                else -> previousState
            }
            is ViewerEvent.StreamStarted -> when (previousState) {
                is ViewerState.Connected -> ViewerState.Streaming(
                    server = previousState.server,
                    streamId = event.streamId,
                    width = event.width,
                    height = event.height
                )
                else -> previousState
            }
            is ViewerEvent.StreamStopped -> when (previousState) {
                is ViewerState.Streaming -> ViewerState.Connected(previousState.server)
                else -> previousState
            }
            is ViewerEvent.FatalError -> ViewerState.Error(event.code, event.message)
            is ViewerEvent.Disconnect -> ViewerState.Disconnected
        }
    }
}
