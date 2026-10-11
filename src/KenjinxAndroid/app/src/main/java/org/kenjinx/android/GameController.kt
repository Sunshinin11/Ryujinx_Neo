package org.kenjinx.android

import android.app.Activity
import android.content.Context
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.widget.FrameLayout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.math.MathUtils
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import com.swordfish.radialgamepad.library.RadialGamePad
import com.swordfish.radialgamepad.library.config.ButtonConfig
import com.swordfish.radialgamepad.library.config.CrossConfig
import com.swordfish.radialgamepad.library.config.CrossContentDescription
import com.swordfish.radialgamepad.library.config.PrimaryDialConfig
import com.swordfish.radialgamepad.library.config.RadialGamePadConfig
import com.swordfish.radialgamepad.library.config.SecondaryDialConfig
import com.swordfish.radialgamepad.library.event.Event
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.merge
import kotlinx.coroutines.flow.shareIn
import kotlinx.coroutines.launch
import org.kenjinx.android.viewmodels.MainViewModel
import org.kenjinx.android.viewmodels.QuickSettings

typealias GamePad = RadialGamePad
typealias GamePadConfig = RadialGamePadConfig

// --- Dummy IDs to disconnect legacy L3/R3 (stick double tap/tap) ---
private const val DUMMY_LEFT_STICK_PRESS_ID  = 10001
private const val DUMMY_RIGHT_STICK_PRESS_ID = 10002

class GameController(var activity: Activity) : IGameController {

    companion object {
        private fun init(context: Context, controller: GameController): View {
            val inflater = LayoutInflater.from(context)
            val parent = FrameLayout(context)
            val view = inflater.inflate(R.layout.game_layout, parent, false)

            val leftContainer  = view.findViewById<FrameLayout>(R.id.leftcontainer)!!
            val rightContainer = view.findViewById<FrameLayout>(R.id.rightcontainer)!!

            leftContainer.addView(controller.leftGamePad)
            rightContainer.addView(controller.rightGamePad)

            leftContainer.addOnLayoutChangeListener { v, _, _, _, _, _, _, _, _ ->
                val scale = QuickSettings(controller.activity).controllerScale.coerceIn(0.5f, 1.5f)
                v.pivotX = 0f
                v.pivotY = v.height.toFloat()
                v.scaleX = scale
                v.scaleY = scale
            }
            rightContainer.addOnLayoutChangeListener { v, _, _, _, _, _, _, _, _ ->
                val scale = QuickSettings(controller.activity).controllerScale.coerceIn(0.5f, 1.5f)
                v.pivotX = v.width.toFloat()
                v.pivotY = v.height.toFloat()
                v.scaleX = scale
                v.scaleY = scale
            }

            leftContainer.post { leftContainer.requestLayout() }
            rightContainer.post { rightContainer.requestLayout() }

            return view
        }

        @Composable
        fun Compose(viewModel: MainViewModel) {
            AndroidView(
                modifier = Modifier.fillMaxSize(),
                factory = { context ->
                    val controller = GameController(viewModel.activity)
                    val c = init(context, controller)
                    viewModel.activity.lifecycleScope.apply {
                        viewModel.activity.lifecycleScope.launch {
                            val events = merge(
                                controller.leftGamePad.events(),
                                controller.rightGamePad.events()
                            ).shareIn(viewModel.activity.lifecycleScope, SharingStarted.Lazily)
                            events.safeCollect {
                                controller.handleEvent(it)
                            }
                        }
                    }
                    controller.controllerView = c
                    viewModel.setGameController(controller)
                    controller.setVisible(QuickSettings(viewModel.activity).useVirtualController)
                    c
                }
            )
        }
    }


    private var controllerView: View? = null
    var leftGamePad: GamePad
    var rightGamePad: GamePad
    var controllerId: Int = -1
    override val isVisible: Boolean
        get() = controllerView?.isVisible ?: false

    init {
        val useSwitchLayout = QuickSettings(activity).useSwitchLayout
        leftGamePad = GamePad(generateConfig(true,  useSwitchLayout), 16f, activity)
        rightGamePad = GamePad(generateConfig(false, useSwitchLayout), 16f, activity)

        leftGamePad.primaryDialMaxSizeDp = 200f
        rightGamePad.primaryDialMaxSizeDp = 200f

        leftGamePad.gravityX = -1f
        leftGamePad.gravityY = 1f
        rightGamePad.gravityX = 1f
        rightGamePad.gravityY = 1f
    }

    override fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            if (isVisible) connect()
        }
    }

    override fun connect() {
        if (controllerId == -1)
            controllerId = KenjinxNative.inputConnectGamepad(0)
    }

    private fun handleEvent(ev: Event) {
        if (controllerId == -1)
            controllerId = KenjinxNative.inputConnectGamepad(0)

        controllerId.apply {
            when (ev) {
                is Event.Button -> {
                    // Ignore legacy L3/R3 via stick press (double tap)
                    if (ev.id == DUMMY_LEFT_STICK_PRESS_ID || ev.id == DUMMY_RIGHT_STICK_PRESS_ID) {
                        return
                    }
                    when (ev.action) {
                        KeyEvent.ACTION_UP   -> KenjinxNative.inputSetButtonReleased(ev.id, this)
                        KeyEvent.ACTION_DOWN -> KenjinxNative.inputSetButtonPressed(ev.id, this)
                    }
                }

                is Event.Direction -> {
                    when (ev.id) {
                        GamePadButtonInputId.DpadUp.ordinal -> {
                            // Horizontal
                            if (ev.xAxis > 0) {
                                KenjinxNative.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, this)
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, this)
                            } else if (ev.xAxis < 0) {
                                KenjinxNative.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, this)
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, this)
                            } else {
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, this)
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, this)
                            }
                            // Vertical
                            if (ev.yAxis < 0) {
                                KenjinxNative.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, this)
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, this)
                            } else if (ev.yAxis > 0) {
                                KenjinxNative.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, this)
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, this)
                            } else {
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, this)
                                KenjinxNative.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, this)
                            }
                        }

                        GamePadButtonInputId.LeftStick.ordinal -> {
                            val setting = QuickSettings(activity)
                            val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                            val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                            KenjinxNative.inputSetStickAxis(1, x, -y, this)
                        }

                        GamePadButtonInputId.RightStick.ordinal -> {
                            val setting = QuickSettings(activity)
                            val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                            val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                            KenjinxNative.inputSetStickAxis(2, x, -y, this)
                        }
                    }
                }
            }
        }
    }
}

suspend fun <T> Flow<T>.safeCollect(block: suspend (T) -> Unit) {
    this.catch { }
        .collect { block(it) }
}

private fun generateConfig(isLeft: Boolean, useSwitchLayout: Boolean): GamePadConfig {
    val distance = 0.3f
    val buttonScale = 1f

    if (isLeft) {
        return GamePadConfig(
            /* ringSegments = */ 12,
            /* Primary (Stick)  */
            // IMPORTANT: pressButtonId -> DUMMY_LEFT_STICK_PRESS_ID, so that double tap does not trigger L3
            PrimaryDialConfig.Stick(
                GamePadButtonInputId.LeftStick.ordinal,
                DUMMY_LEFT_STICK_PRESS_ID,
                setOf(),
                "LeftStick",
                null
            ),
            listOf(
                // D-Pad
                SecondaryDialConfig.Cross(
                    /* sector */ 10,
                    /* size   */ 3,
                    /* gap    */ 2.5f,
                    distance,
                    CrossConfig(
                        GamePadButtonInputId.DpadUp.ordinal,
                        CrossConfig.Shape.STANDARD,
                        null,
                        setOf(),
                        CrossContentDescription(),
                        true,
                        null
                    ),
                    SecondaryDialConfig.RotationProcessor()
                ),

                // Minus
                SecondaryDialConfig.SingleButton(
                    /* sector */ 1,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.Minus.ordinal,
                        "-",
                        true,
                        null,
                        "Minus",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),

                // L Bumper
                SecondaryDialConfig.DoubleButton(
                    /* sector */ 2,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.LeftShoulder.ordinal,
                        "L",
                        true,
                        null,
                        "LeftBumper",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),

                // ZL Trigger
                SecondaryDialConfig.SingleButton(
                    /* sector */ 9,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.LeftTrigger.ordinal,
                        "ZL",
                        true,
                        null,
                        "LeftTrigger",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),

                // L3 as a separate button
                SecondaryDialConfig.SingleButton(
                    /* sector */ 1,
                    buttonScale,
                    1.5f,
                    ButtonConfig(
                        GamePadButtonInputId.LeftStickButton.ordinal,
                        "L3",
                        true,
                        null,
                        "LeftStickButton",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
            )
        )
    } else {
        return GamePadConfig(
            /* ringSegments = */ 12,
            /* Primary (ABXY) */
            PrimaryDialConfig.PrimaryButtons(
                if (useSwitchLayout) {
                    // Switch/Nintendo: A=Right, X=Top, Y=Left, B=Bottom
                    listOf(
                        ButtonConfig(GamePadButtonInputId.A.ordinal, "A", true, null, "A", setOf(), true, null), // Right
                        ButtonConfig(GamePadButtonInputId.X.ordinal, "X", true, null, "X", setOf(), true, null), // Top
                        ButtonConfig(GamePadButtonInputId.Y.ordinal, "Y", true, null, "Y", setOf(), true, null), // Left
                        ButtonConfig(GamePadButtonInputId.B.ordinal, "B", true, null, "B", setOf(), true, null)  // Bottom
                    )
                } else {
                    // Xbox Style: B=Right, Y=Top, X=Left, A=Bottom
                    listOf(
                        ButtonConfig(GamePadButtonInputId.B.ordinal, "B", true, null, "B", setOf(), true, null), // Right
                        ButtonConfig(GamePadButtonInputId.Y.ordinal, "Y", true, null, "Y", setOf(), true, null), // Top
                        ButtonConfig(GamePadButtonInputId.X.ordinal, "X", true, null, "X", setOf(), true, null), // Left
                        ButtonConfig(GamePadButtonInputId.A.ordinal, "A", true, null, "A", setOf(), true, null)  // Bottom
                    )
                },
                null,
                0f,
                true,
                null
            ),
            listOf(
                // Right stick (unchanged)
                // IMPORTANT: pressButtonId -> DUMMY_RIGHT_STICK_PRESS_ID, so that double tap does not trigger R3
                SecondaryDialConfig.Stick(
                    /* sector */ 7,
                    /* size   */ 2,
                    /* gap    */ 2f,
                    distance,
                    GamePadButtonInputId.RightStick.ordinal,
                    DUMMY_RIGHT_STICK_PRESS_ID,
                    null,
                    setOf(),
                    "RightStick",
                    SecondaryDialConfig.RotationProcessor()
                ),

                // Plus
                SecondaryDialConfig.SingleButton(
                    /* sector */ 6,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.Plus.ordinal,
                        "+",
                        true,
                        null,
                        "Plus",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),

                // R Bumper
                SecondaryDialConfig.DoubleButton(
                    /* sector */ 3,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.RightShoulder.ordinal,
                        "R",
                        true,
                        null,
                        "RightBumper",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),

                // ZR Trigger
                SecondaryDialConfig.SingleButton(
                    /* sector */ 9,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.RightTrigger.ordinal,
                        "ZR",
                        true,
                        null,
                        "RightTrigger",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),

                // R3 as a separate button
                SecondaryDialConfig.SingleButton(
                    /* sector */ 5,
                    buttonScale,
                    1.5f,
                    ButtonConfig(
                        GamePadButtonInputId.RightStickButton.ordinal,
                        "R3",
                        true,
                        null,
                        "RightStickButton",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
            )
        )
    }
}
