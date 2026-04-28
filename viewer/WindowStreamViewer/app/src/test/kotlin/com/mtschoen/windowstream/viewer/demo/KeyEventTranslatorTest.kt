package com.mtschoen.windowstream.viewer.demo

import com.mtschoen.windowstream.viewer.control.ControlMessage
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

class KeyEventTranslatorTest {

    private val streamId = 42
    private val translator = KeyEventTranslator(streamId)

    // ─── translate() — Unicode path ──────────────────────────────────────────

    @Test
    fun `translate returns unicode key event when unicodeChar is non-zero`() {
        val result = translator.translate(androidKeyCode = 0, unicodeChar = 'A'.code, isDown = true)
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 'A'.code, isUnicode = true, isDown = true),
            result
        )
    }

    @Test
    fun `translate encodes isDown false for unicode key up`() {
        val result = translator.translate(androidKeyCode = 0, unicodeChar = 'z'.code, isDown = false)
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 'z'.code, isUnicode = true, isDown = false),
            result
        )
    }

    @Test
    fun `translate prefers unicodeChar over androidKeyCode when both non-zero`() {
        // If unicodeChar is non-zero the key code path is skipped.
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_ENTER,
            unicodeChar = 'a'.code,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 'a'.code, isUnicode = true, isDown = true),
            result
        )
    }

    // ─── translate() — mapped virtual-key path ───────────────────────────────

    @Test
    fun `translate maps ENTER to VK_RETURN (0x0D)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_ENTER,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x0D, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps DEL to VK_BACK (0x08)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_DEL,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x08, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps FORWARD_DEL to VK_DELETE (0x2E)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_FORWARD_DEL,
            unicodeChar = 0,
            isDown = false
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x2E, isUnicode = false, isDown = false),
            result
        )
    }

    @Test
    fun `translate maps TAB to VK_TAB (0x09)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_TAB,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x09, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps ESCAPE to VK_ESCAPE (0x1B)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_ESCAPE,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x1B, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps DPAD_LEFT to VK_LEFT (0x25)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_DPAD_LEFT,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x25, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps DPAD_UP to VK_UP (0x26)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_DPAD_UP,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x26, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps DPAD_RIGHT to VK_RIGHT (0x27)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_DPAD_RIGHT,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x27, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps DPAD_DOWN to VK_DOWN (0x28)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_DPAD_DOWN,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x28, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps HOME to VK_HOME (0x24)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_HOME,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x24, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate maps MOVE_END to VK_END (0x23)`() {
        val result = translator.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_MOVE_END,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(
            ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x23, isUnicode = false, isDown = true),
            result
        )
    }

    @Test
    fun `translate returns null for unmapped key code with no unicode`() {
        // Use an arbitrary unmapped key code (KEYCODE_CAMERA = 27 — not in the map).
        val result = translator.translate(androidKeyCode = 27, unicodeChar = 0, isDown = true)
        assertNull(result)
    }

    // ─── streamId routing ────────────────────────────────────────────────────

    @Test
    fun `translate stamps result with the configured streamId`() {
        val translatorForStream7 = KeyEventTranslator(streamId = 7)
        val result = translatorForStream7.translate(
            androidKeyCode = KeyEventTranslator.KEYCODE_ENTER,
            unicodeChar = 0,
            isDown = true
        )
        assertEquals(7, result?.streamId)
    }

    // ─── unicodeKeyPair ───────────────────────────────────────────────────────

    @Test
    fun `unicodeKeyPair returns down then up event for character`() {
        val pairs = translator.unicodeKeyPair('H')
        assertEquals(2, pairs.size)
        assertEquals(ControlMessage.KeyEvent(streamId = streamId, keyCode = 'H'.code, isUnicode = true, isDown = true), pairs[0])
        assertEquals(ControlMessage.KeyEvent(streamId = streamId, keyCode = 'H'.code, isUnicode = true, isDown = false), pairs[1])
    }

    @Test
    fun `unicodeKeyPair stamps the active streamId`() {
        val translatorForStream3 = KeyEventTranslator(streamId = 3)
        val pairs = translatorForStream3.unicodeKeyPair('x')
        assertEquals(3, pairs[0].streamId)
        assertEquals(3, pairs[1].streamId)
    }

    // ─── backspaceKeyPair ─────────────────────────────────────────────────────

    @Test
    fun `backspaceKeyPair returns VK_BACK (0x08) down then up`() {
        val pairs = translator.backspaceKeyPair()
        assertEquals(2, pairs.size)
        assertEquals(ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x08, isUnicode = false, isDown = true), pairs[0])
        assertEquals(ControlMessage.KeyEvent(streamId = streamId, keyCode = 0x08, isUnicode = false, isDown = false), pairs[1])
    }

    @Test
    fun `backspaceKeyPair stamps the active streamId`() {
        val translatorForStream5 = KeyEventTranslator(streamId = 5)
        val pairs = translatorForStream5.backspaceKeyPair()
        assertEquals(5, pairs[0].streamId)
        assertEquals(5, pairs[1].streamId)
    }

    // ─── companion androidKeyCodeToWindowsVirtualKey ─────────────────────────

    @Test
    fun `androidKeyCodeToWindowsVirtualKey returns null for unknown key`() {
        assertNull(KeyEventTranslator.androidKeyCodeToWindowsVirtualKey(9999))
    }

    @Test
    fun `androidKeyCodeToWindowsVirtualKey maps all expected keys`() {
        val expectedMappings = mapOf(
            KeyEventTranslator.KEYCODE_ENTER to 0x0D,
            KeyEventTranslator.KEYCODE_DEL to 0x08,
            KeyEventTranslator.KEYCODE_FORWARD_DEL to 0x2E,
            KeyEventTranslator.KEYCODE_TAB to 0x09,
            KeyEventTranslator.KEYCODE_ESCAPE to 0x1B,
            KeyEventTranslator.KEYCODE_DPAD_LEFT to 0x25,
            KeyEventTranslator.KEYCODE_DPAD_UP to 0x26,
            KeyEventTranslator.KEYCODE_DPAD_RIGHT to 0x27,
            KeyEventTranslator.KEYCODE_DPAD_DOWN to 0x28,
            KeyEventTranslator.KEYCODE_HOME to 0x24,
            KeyEventTranslator.KEYCODE_MOVE_END to 0x23
        )
        expectedMappings.forEach { (androidKey, windowsKey) ->
            assertEquals(
                windowsKey,
                KeyEventTranslator.androidKeyCodeToWindowsVirtualKey(androidKey),
                "Expected androidKeyCode=$androidKey to map to windowsVirtualKey=0x${windowsKey.toString(16)}"
            )
        }
    }
}
