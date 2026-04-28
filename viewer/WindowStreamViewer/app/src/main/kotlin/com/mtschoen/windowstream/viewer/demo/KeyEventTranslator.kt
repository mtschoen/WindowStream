package com.mtschoen.windowstream.viewer.demo

import com.mtschoen.windowstream.viewer.control.ControlMessage

/**
 * Translates Android key event data into [ControlMessage.KeyEvent] messages ready to send
 * over the control connection.
 *
 * Extracted from [DemoActivity]'s inline translation logic so it can be unit-tested
 * on the JVM without the Android runtime. Callers supply raw integer values from
 * [android.view.KeyEvent] rather than the Android type itself, keeping this class
 * dependency-free for JVM unit tests.
 *
 * [streamId] identifies the stream that key events should be attributed to; callers
 * are responsible for supplying the active panel's streamId.
 */
class KeyEventTranslator(private val streamId: Int) {
    /**
     * Translates a single key event into a [ControlMessage.KeyEvent], or returns `null`
     * if the key code is not mapped (caller should pass to default dispatch).
     *
     * @param androidKeyCode the [android.view.KeyEvent.getKeyCode] value.
     * @param unicodeChar the resolved Unicode character from [android.view.KeyEvent.getUnicodeChar],
     *        or 0 if the key has no printable character.
     * @param isDown `true` for [android.view.KeyEvent.ACTION_DOWN], `false` for ACTION_UP.
     */
    fun translate(androidKeyCode: Int, unicodeChar: Int, isDown: Boolean): ControlMessage.KeyEvent? {
        if (unicodeChar != 0) {
            return ControlMessage.KeyEvent(
                streamId = streamId,
                keyCode = unicodeChar,
                isUnicode = true,
                isDown = isDown
            )
        }
        val windowsVirtualKey: Int = androidKeyCodeToWindowsVirtualKey(androidKeyCode) ?: return null
        return ControlMessage.KeyEvent(
            streamId = streamId,
            keyCode = windowsVirtualKey,
            isUnicode = false,
            isDown = isDown
        )
    }

    /**
     * Creates a Unicode character key event pair (down + up) for a single character.
     * Used by the soft-keyboard text watcher when a character is appended to the EditText.
     */
    fun unicodeKeyPair(character: Char): List<ControlMessage.KeyEvent> = listOf(
        ControlMessage.KeyEvent(streamId = streamId, keyCode = character.code, isUnicode = true, isDown = true),
        ControlMessage.KeyEvent(streamId = streamId, keyCode = character.code, isUnicode = true, isDown = false)
    )

    /**
     * Creates a backspace key event pair (down + up) using the VK_BACK Windows virtual key (0x08).
     */
    fun backspaceKeyPair(): List<ControlMessage.KeyEvent> = listOf(
        ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x08, isUnicode = false, isDown = true),
        ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x08, isUnicode = false, isDown = false)
    )

    companion object {
        // Android KeyEvent key code constants — reproduced here so this class is
        // usable in JVM unit tests without the Android runtime. These values are
        // stable API constants defined by the Android platform.
        const val KEYCODE_ENTER: Int = 66
        const val KEYCODE_DEL: Int = 67
        const val KEYCODE_FORWARD_DEL: Int = 112
        const val KEYCODE_TAB: Int = 61
        const val KEYCODE_ESCAPE: Int = 111
        const val KEYCODE_DPAD_LEFT: Int = 21
        const val KEYCODE_DPAD_UP: Int = 19
        const val KEYCODE_DPAD_RIGHT: Int = 22
        const val KEYCODE_DPAD_DOWN: Int = 20
        const val KEYCODE_HOME: Int = 3
        const val KEYCODE_MOVE_END: Int = 123

        /**
         * Maps Android key codes to Windows Virtual Key codes for non-printable keys.
         * Returns `null` for unmapped keys.
         */
        fun androidKeyCodeToWindowsVirtualKey(androidKeyCode: Int): Int? = when (androidKeyCode) {
            KEYCODE_ENTER -> 0x0D
            KEYCODE_DEL -> 0x08
            KEYCODE_FORWARD_DEL -> 0x2E
            KEYCODE_TAB -> 0x09
            KEYCODE_ESCAPE -> 0x1B
            KEYCODE_DPAD_LEFT -> 0x25
            KEYCODE_DPAD_UP -> 0x26
            KEYCODE_DPAD_RIGHT -> 0x27
            KEYCODE_DPAD_DOWN -> 0x28
            KEYCODE_HOME -> 0x24
            KEYCODE_MOVE_END -> 0x23
            else -> null
        }
    }
}
