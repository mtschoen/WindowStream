package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlin.reflect.KClass

/**
 * Suspend until either an instance of [targetType] arrives, in which case it
 * is returned, OR a [ControlMessage.ErrorMessage] arrives, in which case
 * [ControlServerErrorException] is thrown. Other message types pass through.
 *
 * Use during initial-handshake message gating (e.g. waiting for
 * `ServerHello` or `StreamStarted`) where the server may legitimately deliver
 * the awaited response or report a server-side error in its place. A plain
 * `filterIsInstance<T>().first()` would silently discard the error and force
 * the caller to wait out the surrounding timeout, surfacing a generic
 * "pipeline cancelled" instead of the real protocol code.
 *
 * Calling with `targetType = ControlMessage.ErrorMessage::class` is
 * unsupported — the error branch wins on type-equality and the call will
 * always throw.
 */
suspend fun <T : ControlMessage> Flow<ControlMessage>.awaitOrError(targetType: KClass<T>): T {
    val message: ControlMessage = first { targetType.isInstance(it) || it is ControlMessage.ErrorMessage }
    if (message is ControlMessage.ErrorMessage) throw ControlServerErrorException(message)
    @Suppress("UNCHECKED_CAST")
    return message as T
}
