using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MCPForUnity.Editor.Tools.Input
{
    /// <summary>
    /// Simulates gameplay input during Play Mode (keyboard, mouse, touch, gamepad, UI).
    /// Requires Play Mode to be active for all actions except get_status.
    ///
    /// KEYBOARD:
    ///   - key_down: Press and hold a key
    ///   - key_up: Release a key
    ///   - key_press: Press + delay + release (async)
    ///
    /// MOUSE:
    ///   - mouse_move: Move mouse to screen coordinates [x, y]
    ///   - mouse_button_down: Press mouse button (0=left, 1=right, 2=middle)
    ///   - mouse_button_up: Release mouse button
    ///   - mouse_click: Click (down + delay + up, async)
    ///   - mouse_scroll: Scroll wheel
    ///
    /// TOUCH (New Input System only):
    ///   - touch: Simulate touch (phase: began/moved/ended/canceled)
    ///
    /// GAMEPAD (New Input System only):
    ///   - gamepad_button: Press/release gamepad button
    ///   - gamepad_axis: Set gamepad axis value
    ///
    /// UI (any input system):
    ///   - click_ui: Click a UI element by name or hierarchy path
    ///
    /// QUERY:
    ///   - get_input_state: Read current input device states
    ///   - get_status: Report capabilities and active input system
    ///
    /// SEQUENCES:
    ///   - send_sequence: Execute timed input sequence (returns job_id)
    ///   - get_sequence_status: Poll sequence job progress
    /// </summary>
    [McpForUnityTool("manage_input", AutoRegister = false, Group = "gameplay")]
    public static class ManageInput
    {
        private static readonly HashSet<string> PlayModeExemptActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_status"
        };

        private static readonly HashSet<string> AllActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "key_down", "key_up", "key_press",
            "mouse_move", "mouse_button_down", "mouse_button_up", "mouse_click", "mouse_scroll",
            "touch",
            "gamepad_button", "gamepad_axis",
            "click_ui",
            "get_input_state", "get_status",
            "send_sequence", "get_sequence_status"
        };

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            if (!AllActions.Contains(action))
            {
                var suggestions = AllActions
                    .Where(a => a.StartsWith(action.Split('_')[0] + "_", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (suggestions.Count > 0)
                    return new ErrorResponse($"Unknown action '{action}'. Similar actions: {string.Join(", ", suggestions)}");

                return new ErrorResponse(
                    $"Unknown action '{action}'. Valid actions: {string.Join(", ", AllActions.OrderBy(a => a))}");
            }

            if (!PlayModeExemptActions.Contains(action) && !EditorApplication.isPlaying)
            {
                return new ErrorResponse(
                    "Input simulation requires Play Mode. Use manage_editor action='play' first.");
            }

            try
            {
                return ExecuteAction(action, p, @params);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Input action '{action}' failed: {e.Message}");
            }
        }

        internal static object ExecuteAction(string action, ToolParams p, JObject @params)
        {
            switch (action)
            {
                // Keyboard
                case "key_down":
                    return HandleKeyDown(p);
                case "key_up":
                    return HandleKeyUp(p);
                case "key_press":
                    return HandleKeyPress(p);

                // Mouse
                case "mouse_move":
                    return HandleMouseMove(p);
                case "mouse_button_down":
                    return HandleMouseButtonDown(p);
                case "mouse_button_up":
                    return HandleMouseButtonUp(p);
                case "mouse_click":
                    return HandleMouseClick(p);
                case "mouse_scroll":
                    return HandleMouseScroll(p);

                // Touch
                case "touch":
                    return HandleTouch(p);

                // Gamepad
                case "gamepad_button":
                    return HandleGamepadButton(p);
                case "gamepad_axis":
                    return HandleGamepadAxis(p);

                // UI
                case "click_ui":
                    return UIInputSimulator.ClickUI(p);

                // Query
                case "get_input_state":
                    return InputSimulator.GetInputState();
                case "get_status":
                    return HandleGetStatus();

                // Sequences
                case "send_sequence":
                    return InputSequenceRunner.StartSequence(@params);
                case "get_sequence_status":
                    return InputSequenceRunner.GetStatus(p);

                default:
                    return new ErrorResponse($"Action '{action}' is recognized but not yet implemented.");
            }
        }

        // --- Keyboard ---

        private static object HandleKeyDown(ToolParams p)
        {
            var keyResult = p.GetRequired("key");
            if (!keyResult.IsSuccess)
                return new ErrorResponse(keyResult.ErrorMessage);

            if (InputSimulator.IsAvailable)
                return InputSimulator.KeyDown(keyResult.Value);

            return LegacyInputBridge.KeyDown(keyResult.Value);
        }

        private static object HandleKeyUp(ToolParams p)
        {
            var keyResult = p.GetRequired("key");
            if (!keyResult.IsSuccess)
                return new ErrorResponse(keyResult.ErrorMessage);

            if (InputSimulator.IsAvailable)
                return InputSimulator.KeyUp(keyResult.Value);

            return LegacyInputBridge.KeyUp(keyResult.Value);
        }

        private static object HandleKeyPress(ToolParams p)
        {
            var keyResult = p.GetRequired("key");
            if (!keyResult.IsSuccess)
                return new ErrorResponse(keyResult.ErrorMessage);

            float duration = p.GetFloat("duration") ?? 0.1f;

            if (InputSimulator.IsAvailable)
                return InputSimulator.KeyPress(keyResult.Value, duration);

            return LegacyInputBridge.KeyPress(keyResult.Value, duration);
        }

        // --- Mouse ---

        private static object HandleMouseMove(ToolParams p)
        {
            var posToken = p.GetRaw("position");
            if (posToken == null)
                return new ErrorResponse("Required parameter 'position' is missing. Provide [x, y] screen coordinates.");

            Vector2 pos;
            try
            {
                var arr = posToken.ToObject<float[]>();
                if (arr == null || arr.Length < 2)
                    return new ErrorResponse("'position' must be an array of [x, y].");
                pos = new Vector2(arr[0], arr[1]);
            }
            catch
            {
                return new ErrorResponse("'position' must be an array of [x, y] floats.");
            }

            if (InputSimulator.IsAvailable)
                return InputSimulator.MouseMove(pos);

            return LegacyInputBridge.MouseMove(pos);
        }

        private static object HandleMouseButtonDown(ToolParams p)
        {
            int button = p.GetInt("button") ?? 0;
            if (button < 0 || button > 2)
                return new ErrorResponse("'button' must be 0 (left), 1 (right), or 2 (middle).");

            if (InputSimulator.IsAvailable)
                return InputSimulator.MouseButtonDown(button);

            return LegacyInputBridge.MouseButtonDown(button);
        }

        private static object HandleMouseButtonUp(ToolParams p)
        {
            int button = p.GetInt("button") ?? 0;
            if (button < 0 || button > 2)
                return new ErrorResponse("'button' must be 0 (left), 1 (right), or 2 (middle).");

            if (InputSimulator.IsAvailable)
                return InputSimulator.MouseButtonUp(button);

            return LegacyInputBridge.MouseButtonUp(button);
        }

        private static object HandleMouseClick(ToolParams p)
        {
            int button = p.GetInt("button") ?? 0;
            float duration = p.GetFloat("duration") ?? 0.05f;

            var posToken = p.GetRaw("position");
            Vector2? pos = null;
            if (posToken != null)
            {
                try
                {
                    var arr = posToken.ToObject<float[]>();
                    if (arr != null && arr.Length >= 2)
                        pos = new Vector2(arr[0], arr[1]);
                }
                catch { /* ignore, will click at current position */ }
            }

            if (InputSimulator.IsAvailable)
                return InputSimulator.MouseClick(button, pos, duration);

            return LegacyInputBridge.MouseClick(button, pos, duration);
        }

        private static object HandleMouseScroll(ToolParams p)
        {
            float delta = p.GetFloat("scroll_delta") ?? p.GetFloat("scrollDelta") ?? 0f;
            if (Mathf.Approximately(delta, 0f))
                return new ErrorResponse("'scroll_delta' is required and must be non-zero.");

            if (InputSimulator.IsAvailable)
                return InputSimulator.MouseScroll(delta);

            return LegacyInputBridge.MouseScroll(delta);
        }

        // --- Touch ---

        private static object HandleTouch(ToolParams p)
        {
            if (!InputSimulator.IsAvailable)
                return new ErrorResponse("Touch simulation requires the New Input System package (com.unity.inputsystem).");

            var phaseStr = p.Get("phase") ?? "began";
            int fingerId = p.GetInt("finger_id") ?? p.GetInt("fingerId") ?? 0;

            var posToken = p.GetRaw("position");
            Vector2 pos = Vector2.zero;
            if (posToken != null)
            {
                try
                {
                    var arr = posToken.ToObject<float[]>();
                    if (arr != null && arr.Length >= 2)
                        pos = new Vector2(arr[0], arr[1]);
                }
                catch { /* default to zero */ }
            }

            return InputSimulator.Touch(fingerId, phaseStr, pos);
        }

        // --- Gamepad ---

        private static object HandleGamepadButton(ToolParams p)
        {
            if (!InputSimulator.IsAvailable)
                return new ErrorResponse("Gamepad simulation requires the New Input System package (com.unity.inputsystem).");

            var buttonResult = p.GetRequired("button_name");
            if (!buttonResult.IsSuccess)
                return new ErrorResponse(buttonResult.ErrorMessage);

            bool pressed = p.GetBool("pressed", true);

            return InputSimulator.GamepadButton(buttonResult.Value, pressed);
        }

        private static object HandleGamepadAxis(ToolParams p)
        {
            if (!InputSimulator.IsAvailable)
                return new ErrorResponse("Gamepad simulation requires the New Input System package (com.unity.inputsystem).");

            var axisResult = p.GetRequired("axis_name");
            if (!axisResult.IsSuccess)
                return new ErrorResponse(axisResult.ErrorMessage);

            var valueToken = p.GetRaw("axis_value") ?? p.GetRaw("axisValue");
            if (valueToken == null)
                return new ErrorResponse("Required parameter 'axis_value' is missing.");

            return InputSimulator.GamepadAxis(axisResult.Value, valueToken);
        }

        // --- Status ---

        private static object HandleGetStatus()
        {
            bool newIS = InputSimulator.IsAvailable;
            bool eventSystem = EventSystem.current != null;

            var supported = new List<string> { "get_status" };
            var limitations = new List<string>();

            if (EditorApplication.isPlaying)
            {
                if (newIS)
                {
                    supported.AddRange(new[]
                    {
                        "key_down", "key_up", "key_press",
                        "mouse_move", "mouse_button_down", "mouse_button_up", "mouse_click", "mouse_scroll",
                        "touch", "gamepad_button", "gamepad_axis",
                        "get_input_state", "send_sequence", "get_sequence_status"
                    });
                }
                else
                {
                    supported.AddRange(new[] { "key_down", "key_up", "key_press", "mouse_move",
                        "mouse_button_down", "mouse_button_up", "mouse_click", "mouse_scroll",
                        "send_sequence", "get_sequence_status" });
                    limitations.Add("Legacy Input Manager detected. Games using Input.GetKey()/GetAxis() may not receive simulated input. " +
                                    "Consider using the New Input System (com.unity.inputsystem) for full simulation support, " +
                                    "or use manage_components set_property to directly drive game logic.");
                    limitations.Add("Touch and gamepad simulation require New Input System package.");
                }

                if (eventSystem)
                    supported.Add("click_ui");
                else
                    limitations.Add("No EventSystem found in scene. click_ui action unavailable.");
            }
            else
            {
                limitations.Add("Not in Play Mode. All input actions except get_status require Play Mode.");
            }

            return new SuccessResponse("Input simulation status.", new
            {
                play_mode = EditorApplication.isPlaying,
                new_input_system = newIS,
                legacy_input = !newIS,
                event_system_present = eventSystem,
                bridge_active = LegacyInputBridge.IsBridgeActive,
                supported_actions = supported,
                limitations = limitations
            });
        }
    }
}
