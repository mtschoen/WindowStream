package com.mtschoen.windowstream.viewer.demo

import android.content.Context
import android.text.InputType
import android.util.AttributeSet
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection

/**
 * Invisible view whose sole purpose is to own an [InputConnection] for the soft
 * keyboard. Unlike a hidden [android.widget.EditText] + [android.text.TextWatcher],
 * this receives direct callbacks for committed text and deletions, avoiding the
 * buffer-length-tracking bugs that plague the TextWatcher approach (composition
 * drift, backspace when buffer is empty, CJK/autocomplete intermediate edits).
 *
 * Callers wire behaviour via [onTextCommitted] and [onDeleteRequested].
 */
class InputProxyView @JvmOverloads constructor(
    context: Context,
    attributeSet: AttributeSet? = null,
    defaultStyleAttribute: Int = 0
) : View(context, attributeSet, defaultStyleAttribute) {

    /** Called when the IME commits one or more characters. */
    var onTextCommitted: ((text: String) -> Unit)? = null

    /** Called when the IME requests deletion of [beforeLength] characters before the cursor. */
    var onDeleteRequested: ((beforeLength: Int) -> Unit)? = null

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    override fun onCheckIsTextEditor(): Boolean = true

    override fun onCreateInputConnection(outAttributes: EditorInfo): InputConnection {
        outAttributes.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        outAttributes.imeOptions = EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_ACTION_NONE
        return ProxyInputConnection(this, fullEditor = false)
    }

    private inner class ProxyInputConnection(
        targetView: View,
        fullEditor: Boolean
    ) : BaseInputConnection(targetView, fullEditor) {

        override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
            val committed = text?.toString() ?: return false
            if (committed.isNotEmpty()) {
                onTextCommitted?.invoke(committed)
            }
            return super.commitText(text, newCursorPosition)
        }

        override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
            if (beforeLength > 0) {
                onDeleteRequested?.invoke(beforeLength)
            }
            return super.deleteSurroundingText(beforeLength, afterLength)
        }

        override fun sendKeyEvent(event: android.view.KeyEvent): Boolean {
            // Some IMEs send raw key events for backspace/enter instead of using
            // deleteSurroundingText / commitText. Forward them via the activity's
            // dispatchKeyEvent so the existing KeyEventTranslator handles them.
            val hostActivity = this@InputProxyView.context as? android.app.Activity
            return if (hostActivity != null) {
                hostActivity.dispatchKeyEvent(event)
            } else {
                super.sendKeyEvent(event)
            }
        }
    }
}
