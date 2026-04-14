from typing import Annotated, Any, Literal, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

# All supported actions grouped by category
KEYBOARD_ACTIONS = ["key_down", "key_up", "key_press"]
MOUSE_ACTIONS = ["mouse_move", "mouse_button_down", "mouse_button_up", "mouse_click", "mouse_scroll"]
TOUCH_ACTIONS = ["touch"]
GAMEPAD_ACTIONS = ["gamepad_button", "gamepad_axis"]
UI_ACTIONS = ["click_ui"]
QUERY_ACTIONS = ["get_input_state", "get_status"]
SEQUENCE_ACTIONS = ["send_sequence", "get_sequence_status"]

ALL_ACTIONS = (
    KEYBOARD_ACTIONS + MOUSE_ACTIONS + TOUCH_ACTIONS +
    GAMEPAD_ACTIONS + UI_ACTIONS + QUERY_ACTIONS + SEQUENCE_ACTIONS
)


@mcp_for_unity_tool(
    group="gameplay",
    description=(
        "Simulates gameplay input during Play Mode (keyboard, mouse, touch, gamepad, UI). "
        "Requires Play Mode to be active (use manage_editor action='play' first). "
        "Best with New Input System package (com.unity.inputsystem); limited legacy Input support. "
        "Actions: key_down, key_up, key_press, mouse_move, mouse_button_down, mouse_button_up, "
        "mouse_click, mouse_scroll, touch, gamepad_button, gamepad_axis, click_ui, "
        "get_input_state, get_status, send_sequence, get_sequence_status."
    ),
    annotations=ToolAnnotations(
        title="Manage Input",
        destructiveHint=True,
    ),
)
async def manage_input(
    ctx: Context,
    action: Annotated[str, "Input action to perform."],
    key: Annotated[
        Optional[str],
        "Key name for keyboard actions (e.g., 'W', 'Space', 'LeftShift', 'Escape').",
    ] = None,
    button: Annotated[
        Optional[int],
        "Mouse button index: 0=left, 1=right, 2=middle.",
    ] = None,
    position: Annotated[
        Optional[list[float]],
        "Screen coordinates [x, y] for mouse_move, mouse_click, or touch.",
    ] = None,
    scroll_delta: Annotated[
        Optional[float],
        "Scroll wheel delta value for mouse_scroll.",
    ] = None,
    phase: Annotated[
        Optional[str],
        "Touch phase: 'began', 'moved', 'ended', 'canceled'.",
    ] = None,
    finger_id: Annotated[
        Optional[int],
        "Touch finger ID (0-9) for touch actions.",
    ] = None,
    button_name: Annotated[
        Optional[str],
        "Gamepad button name (e.g., 'south'/'a', 'north'/'y', 'start', 'leftShoulder').",
    ] = None,
    pressed: Annotated[
        Optional[bool],
        "Whether button is pressed (true) or released (false). Default true.",
    ] = None,
    axis_name: Annotated[
        Optional[str],
        "Gamepad axis name: 'leftStick', 'rightStick', 'leftTrigger', 'rightTrigger'.",
    ] = None,
    axis_value: Annotated[
        Optional[list[float] | float],
        "Axis value: [x, y] for sticks, float for triggers.",
    ] = None,
    element_name: Annotated[
        Optional[str],
        "UI element name or hierarchy path for click_ui (e.g., 'StartButton', 'Canvas/Panel/Button').",
    ] = None,
    duration: Annotated[
        Optional[float],
        "Duration in seconds for key_press/mouse_click (default 0.1s for keys, 0.05s for mouse).",
    ] = None,
    sequence: Annotated[
        Optional[list[dict]],
        "Array of steps for send_sequence. Each step: {action, params?, delay?}.",
    ] = None,
    job_id: Annotated[
        Optional[str],
        "Job ID for get_sequence_status polling.",
    ] = None,
    wait_timeout: Annotated[
        Optional[int],
        "Seconds to wait for sequence completion in get_sequence_status.",
    ] = None,
) -> dict[str, Any]:
    """Simulate gameplay input during Play Mode."""

    action_normalized = action.lower()

    if action_normalized not in ALL_ACTIONS:
        # Find closest matches by prefix
        prefix = action_normalized.split("_")[0] + "_" if "_" in action_normalized else ""
        available_by_prefix = {
            "key_": KEYBOARD_ACTIONS,
            "mouse_": MOUSE_ACTIONS,
            "touch": TOUCH_ACTIONS,
            "gamepad_": GAMEPAD_ACTIONS,
            "click_": UI_ACTIONS,
            "get_": QUERY_ACTIONS,
            "send_": SEQUENCE_ACTIONS,
        }
        suggestions = available_by_prefix.get(prefix, [])
        if suggestions:
            return {
                "success": False,
                "message": f"Unknown action '{action}'. Available {prefix}* actions: {', '.join(suggestions)}",
            }
        return {
            "success": False,
            "message": (
                f"Unknown action '{action}'. Valid actions: {', '.join(sorted(ALL_ACTIONS))}. "
                "Use action='get_status' to check available capabilities."
            ),
        }

    unity_instance = await get_unity_instance_from_context(ctx)

    # Build params dict, forwarding all non-None parameters
    params_dict: dict[str, Any] = {"action": action_normalized}

    if key is not None:
        params_dict["key"] = key
    if button is not None:
        params_dict["button"] = button
    if position is not None:
        params_dict["position"] = position
    if scroll_delta is not None:
        params_dict["scroll_delta"] = scroll_delta
    if phase is not None:
        params_dict["phase"] = phase
    if finger_id is not None:
        params_dict["finger_id"] = finger_id
    if button_name is not None:
        params_dict["button_name"] = button_name
    if pressed is not None:
        params_dict["pressed"] = pressed
    if axis_name is not None:
        params_dict["axis_name"] = axis_name
    if axis_value is not None:
        params_dict["axis_value"] = axis_value
    if element_name is not None:
        params_dict["element_name"] = element_name
    if duration is not None:
        params_dict["duration"] = duration
    if sequence is not None:
        params_dict["sequence"] = sequence
    if job_id is not None:
        params_dict["job_id"] = job_id

    params_dict = {k: v for k, v in params_dict.items() if v is not None}

    # For get_sequence_status with wait_timeout, implement server-side polling
    if action_normalized == "get_sequence_status" and wait_timeout and wait_timeout > 0:
        import asyncio
        import time

        deadline = time.monotonic() + wait_timeout
        poll_interval = 2.0

        while True:
            result = await send_with_unity_instance(
                async_send_command_with_retry,
                unity_instance,
                "manage_input",
                params_dict,
            )

            if not isinstance(result, dict):
                return {"success": False, "message": str(result)}

            # Check if terminal status
            data = result.get("data", {})
            status = data.get("status", "")
            if status in ("completed", "failed", "cancelled"):
                return result

            if time.monotonic() >= deadline:
                return result

            await asyncio.sleep(min(poll_interval, deadline - time.monotonic()))
    else:
        result = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "manage_input",
            params_dict,
        )

        return result if isinstance(result, dict) else {"success": False, "message": str(result)}
