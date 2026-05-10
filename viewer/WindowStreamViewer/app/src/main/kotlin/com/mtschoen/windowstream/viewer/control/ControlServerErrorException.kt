package com.mtschoen.windowstream.viewer.control

/**
 * Thrown by [awaitOrError] when the server delivers a [ControlMessage.ErrorMessage]
 * while the caller was waiting for a different message type. Carries the
 * original [ControlMessage.ErrorMessage] so callers can inspect the protocol
 * code (e.g. `WINDOW_NOT_FOUND`, `CAPACITY_EXCEEDED`) instead of waiting out
 * the surrounding handshake timeout.
 */
class ControlServerErrorException(
    val errorMessage: ControlMessage.ErrorMessage
) : RuntimeException("server error ${errorMessage.code}: ${errorMessage.message}")
