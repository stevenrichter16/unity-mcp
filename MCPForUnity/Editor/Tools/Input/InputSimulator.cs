using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Input;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    /// <summary>
    /// Simulates input via the New Input System (com.unity.inputsystem) using reflection.
    /// All Input System types are resolved at runtime to avoid compile errors in projects
    /// that don't have the package installed.
    /// </summary>
    internal static class InputSimulator
    {
        // Resolved types
        private static Type _inputSystemType;
        private static Type _inputStateType;
        private static Type _keyboardType;
        private static Type _mouseType;
        private static Type _touchscreenType;
        private static Type _gamepadType;
        private static Type _keyEnum;
        private static Type _gamepadButtonEnum;

        // Cached methods/properties
        private static PropertyInfo _keyboardCurrentProp;
        private static PropertyInfo _mouseCurrentProp;
        private static PropertyInfo _touchscreenCurrentProp;
        private static PropertyInfo _gamepadCurrentProp;
        private static MethodInfo _inputStateChangeMethod;
        private static MethodInfo _queueStateEventMethod;
        private static Type _inputUpdateTypeEnum;
        private static Type _inputEventPtrType;
        private static Type _keyboardStateType;
        private static Type _mouseStateType;
        private static MethodInfo _keyboardStateSetMethod;
        private static MethodInfo _mouseStateWithButtonMethod;
        private static Type _mouseButtonEnum;
        private static object _defaultUpdateType;
        private static object _defaultEventPtr;

        // Key name mapping (built from Key enum via reflection)
        private static Dictionary<string, object> _keyNameMap;

        // Gamepad button name mapping
        private static Dictionary<string, string> _gamepadButtonMap;

        private static bool _resolved;
        private static bool _available;

        public static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _available;
            }
        }

        private static bool EnsureResolved()
        {
            if (_resolved) return _available;
            _resolved = true;

            _inputSystemType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            if (_inputSystemType == null)
            {
                _available = false;
                return false;
            }

            _inputStateType = Type.GetType("UnityEngine.InputSystem.LowLevel.InputState, Unity.InputSystem");
            _keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            _mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
            _touchscreenType = Type.GetType("UnityEngine.InputSystem.Touchscreen, Unity.InputSystem");
            _gamepadType = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
            _keyEnum = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            _gamepadButtonEnum = Type.GetType("UnityEngine.InputSystem.LowLevel.GamepadButton, Unity.InputSystem");

            _keyboardCurrentProp = _keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _mouseCurrentProp = _mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _touchscreenCurrentProp = _touchscreenType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _gamepadCurrentProp = _gamepadType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

            // Resolve state types for QueueStateEvent approach (needed for bitfield controls like keys)
            _keyboardStateType = Type.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState, Unity.InputSystem");
            _mouseStateType = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");

            // KeyboardState.Set(Key, bool) method
            if (_keyboardStateType != null)
                _keyboardStateSetMethod = _keyboardStateType.GetMethod("Set", new[] { _keyEnum, typeof(bool) });

            // MouseState.WithButton(MouseButton, bool) method
            _mouseButtonEnum = Type.GetType("UnityEngine.InputSystem.LowLevel.MouseButton, Unity.InputSystem");
            if (_mouseStateType != null && _mouseButtonEnum != null)
                _mouseStateWithButtonMethod = _mouseStateType.GetMethod("WithButton", new[] { _mouseButtonEnum, typeof(bool) });

            // InputSystem.QueueStateEvent<TState>(InputDevice, TState, double) — the main simulation API
            var queueMethods = _inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "QueueStateEvent" && m.IsGenericMethod)
                .ToArray();
            foreach (var method in queueMethods)
            {
                var methodParams = method.GetParameters();
                // Looking for (InputDevice, TState, double time = -1)
                if (methodParams.Length >= 2 && !methodParams[1].ParameterType.IsByRef)
                {
                    _queueStateEventMethod = method;
                    break;
                }
            }

            // Also resolve InputState.Change for non-bitfield controls (mouse position, gamepad axes)
            if (_inputStateType != null)
            {
                _inputUpdateTypeEnum = Type.GetType("UnityEngine.InputSystem.LowLevel.InputUpdateType, Unity.InputSystem");
                _inputEventPtrType = Type.GetType("UnityEngine.InputSystem.LowLevel.InputEventPtr, Unity.InputSystem");

                if (_inputUpdateTypeEnum != null)
                    _defaultUpdateType = Enum.Parse(_inputUpdateTypeEnum, "Default");
                if (_inputEventPtrType != null)
                    _defaultEventPtr = Activator.CreateInstance(_inputEventPtrType);

                var changeMethods = _inputStateType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Change" && m.IsGenericMethod)
                    .ToArray();

                foreach (var method in changeMethods)
                {
                    var methodParams = method.GetParameters();
                    if (methodParams.Length == 4 && !methodParams[1].ParameterType.IsByRef)
                    {
                        _inputStateChangeMethod = method;
                        break;
                    }
                }
            }

            BuildKeyNameMap();
            BuildGamepadButtonMap();

            _available = _keyboardCurrentProp != null && _mouseCurrentProp != null;
            return _available;
        }

        private static void BuildKeyNameMap()
        {
            _keyNameMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (_keyEnum == null) return;

            foreach (var value in Enum.GetValues(_keyEnum))
            {
                string name = Enum.GetName(_keyEnum, value);
                if (name != null && name != "None" && name != "IMESelected")
                {
                    _keyNameMap[name] = value;
                    // Also add common aliases
                    _keyNameMap[name.ToLowerInvariant()] = value;
                }
            }

            // Add common aliases that don't match enum names directly
            AddKeyAlias("ctrl", "LeftCtrl");
            AddKeyAlias("control", "LeftCtrl");
            AddKeyAlias("shift", "LeftShift");
            AddKeyAlias("alt", "LeftAlt");
            AddKeyAlias("meta", "LeftMeta");
            AddKeyAlias("cmd", "LeftMeta");
            AddKeyAlias("command", "LeftMeta");
            AddKeyAlias("win", "LeftWindows");
            AddKeyAlias("windows", "LeftWindows");
            AddKeyAlias("return", "Enter");
            AddKeyAlias("esc", "Escape");
            AddKeyAlias("del", "Delete");
            AddKeyAlias("ins", "Insert");
            AddKeyAlias("pgup", "PageUp");
            AddKeyAlias("pgdn", "PageDown");
            AddKeyAlias("pgdown", "PageDown");
            AddKeyAlias("up", "UpArrow");
            AddKeyAlias("down", "DownArrow");
            AddKeyAlias("left", "LeftArrow");
            AddKeyAlias("right", "RightArrow");
            AddKeyAlias("lshift", "LeftShift");
            AddKeyAlias("rshift", "RightShift");
            AddKeyAlias("lctrl", "LeftCtrl");
            AddKeyAlias("rctrl", "RightCtrl");
            AddKeyAlias("lalt", "LeftAlt");
            AddKeyAlias("ralt", "RightAlt");
        }

        private static void AddKeyAlias(string alias, string keyName)
        {
            if (_keyNameMap.TryGetValue(keyName, out var value))
                _keyNameMap[alias] = value;
        }

        private static void BuildGamepadButtonMap()
        {
            _gamepadButtonMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "south", "buttonSouth" }, { "a", "buttonSouth" }, { "cross", "buttonSouth" },
                { "north", "buttonNorth" }, { "y", "buttonNorth" }, { "triangle", "buttonNorth" },
                { "east", "buttonEast" }, { "b", "buttonEast" }, { "circle", "buttonEast" },
                { "west", "buttonWest" }, { "x", "buttonWest" }, { "square", "buttonWest" },
                { "start", "startButton" }, { "select", "selectButton" },
                { "leftstick", "leftStickButton" }, { "rightstick", "rightStickButton" },
                { "leftshoulder", "leftShoulder" }, { "l1", "leftShoulder" },
                { "rightshoulder", "rightShoulder" }, { "r1", "rightShoulder" },
                { "lefttrigger", "leftTrigger" }, { "l2", "leftTrigger" },
                { "righttrigger", "rightTrigger" }, { "r2", "rightTrigger" },
                { "dpad/up", "dpad/up" }, { "dpad/down", "dpad/down" },
                { "dpad/left", "dpad/left" }, { "dpad/right", "dpad/right" },
            };
        }

        /// <summary>
        /// Invoke InputState.Change with the correct 4-parameter signature.
        /// </summary>
        private static void InvokeStateChange(object control, object value, Type valueType)
        {
            if (_inputStateChangeMethod == null) return;
            var genericMethod = _inputStateChangeMethod.MakeGenericMethod(valueType);
            genericMethod.Invoke(null, new[] { control, value, _defaultUpdateType, _defaultEventPtr });
        }

        [InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            _resolved = false;
            _available = false;
            _keyNameMap = null;
        }

        // --- Key name to KeyCode mapping for bridge ---

        private static Dictionary<string, KeyCode> _keyNameToKeyCode;

        private static void BuildKeyNameToKeyCodeMap()
        {
            _keyNameToKeyCode = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
                _keyNameToKeyCode[kc.ToString()] = kc;

            // Common aliases
            _keyNameToKeyCode["ctrl"] = KeyCode.LeftControl;
            _keyNameToKeyCode["shift"] = KeyCode.LeftShift;
            _keyNameToKeyCode["alt"] = KeyCode.LeftAlt;
            _keyNameToKeyCode["enter"] = KeyCode.Return;
            _keyNameToKeyCode["esc"] = KeyCode.Escape;
            _keyNameToKeyCode["del"] = KeyCode.Delete;
            _keyNameToKeyCode["up"] = KeyCode.UpArrow;
            _keyNameToKeyCode["down"] = KeyCode.DownArrow;
            _keyNameToKeyCode["left"] = KeyCode.LeftArrow;
            _keyNameToKeyCode["right"] = KeyCode.RightArrow;

            for (char c = 'A'; c <= 'Z'; c++)
                _keyNameToKeyCode[c.ToString()] = (KeyCode)System.Char.ToLower(c);
            for (char c = '0'; c <= '9'; c++)
                _keyNameToKeyCode[c.ToString()] = (KeyCode)((int)KeyCode.Alpha0 + (c - '0'));
        }

        private static bool TryGetKeyCode(string keyName, out KeyCode keyCode)
        {
            if (_keyNameToKeyCode == null) BuildKeyNameToKeyCodeMap();
            return _keyNameToKeyCode.TryGetValue(keyName, out keyCode);
        }

        // --- Keyboard ---

        public static object KeyDown(string keyName)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("New Input System not available.");

            if (!_keyNameMap.ContainsKey(keyName))
                return new ErrorResponse($"Unknown key '{keyName}'. Available keys include: {string.Join(", ", _keyNameMap.Keys.Take(30))}...");

            if (!TryGetKeyCode(keyName, out var keyCode))
                return new ErrorResponse($"Could not map key '{keyName}' to KeyCode.");

            // Route through bridge command queue — bridge re-queues state every frame
            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.KeyDown,
                KeyCode = keyCode
            });

            return new SuccessResponse($"Key pressed successfully.", new
            {
                key = keyName,
                state = "down"
            });
        }

        public static object KeyUp(string keyName)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("New Input System not available.");

            if (!_keyNameMap.ContainsKey(keyName))
                return new ErrorResponse($"Unknown key '{keyName}'.");

            if (!TryGetKeyCode(keyName, out var keyCode))
                return new ErrorResponse($"Could not map key '{keyName}' to KeyCode.");

            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.KeyUp,
                KeyCode = keyCode
            });

            return new SuccessResponse($"Key released successfully.", new
            {
                key = keyName,
                state = "up"
            });
        }

        public static object KeyPress(string keyName, float duration)
        {
            var downResult = KeyDown(keyName);
            if (downResult is ErrorResponse)
                return downResult;

            // Schedule key_up after duration using EditorApplication.delayCall
            var capturedKey = keyName;
            double targetTime = EditorApplication.timeSinceStartup + duration;

            void CheckAndRelease()
            {
                if (EditorApplication.timeSinceStartup >= targetTime)
                {
                    KeyUp(capturedKey);
                }
                else
                {
                    EditorApplication.delayCall += CheckAndRelease;
                }
            }

            EditorApplication.delayCall += CheckAndRelease;

            return new SuccessResponse($"Key '{keyName}' pressed. Will release after {duration}s.");
        }

        private static object SetKeyState(object keyboard, object keyEnumValue, bool pressed)
        {
            try
            {
                // Keyboard keys are bitfield controls — can't use InputState.Change on individual keys.
                // Must use InputSystem.QueueStateEvent(keyboard, KeyboardState) with the key set.
                if (_keyboardStateType == null || _keyboardStateSetMethod == null || _queueStateEventMethod == null)
                    return new ErrorResponse("Could not resolve KeyboardState or QueueStateEvent via reflection.");

                // Create KeyboardState struct and set the target key
                var state = Activator.CreateInstance(_keyboardStateType);
                _keyboardStateSetMethod.Invoke(state, new[] { keyEnumValue, pressed });

                // Call InputSystem.QueueStateEvent<KeyboardState>(keyboard, state)
                var genericQueue = _queueStateEventMethod.MakeGenericMethod(_keyboardStateType);
                genericQueue.Invoke(null, new[] { keyboard, state, (object)(-1.0) });

                return new SuccessResponse($"Key {(pressed ? "pressed" : "released")} successfully.", new
                {
                    key = keyEnumValue.ToString(),
                    state = pressed ? "down" : "up"
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to set key state: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // --- Mouse ---

        public static object MouseMove(Vector2 position)
        {
            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.MouseMove,
                Position = position
            });

            return new SuccessResponse($"Mouse moved to ({position.x}, {position.y}).", new
            {
                position = new[] { position.x, position.y }
            });
        }

        public static object MouseButtonDown(int button)
        {
            return SetMouseButton(button, true);
        }

        public static object MouseButtonUp(int button)
        {
            return SetMouseButton(button, false);
        }

        public static object MouseClick(int button, Vector2? position, float duration)
        {
            if (position.HasValue)
            {
                var moveResult = MouseMove(position.Value);
                if (moveResult is ErrorResponse)
                    return moveResult;
            }

            var downResult = MouseButtonDown(button);
            if (downResult is ErrorResponse)
                return downResult;

            double targetTime = EditorApplication.timeSinceStartup + duration;
            int capturedButton = button;

            void CheckAndRelease()
            {
                if (EditorApplication.timeSinceStartup >= targetTime)
                {
                    MouseButtonUp(capturedButton);
                }
                else
                {
                    EditorApplication.delayCall += CheckAndRelease;
                }
            }

            EditorApplication.delayCall += CheckAndRelease;

            string posStr = position.HasValue ? $" at ({position.Value.x}, {position.Value.y})" : "";
            return new SuccessResponse($"Mouse button {button} clicked{posStr}.", new
            {
                button,
                position = position.HasValue ? new[] { position.Value.x, position.Value.y } : null,
                release_after = duration
            });
        }

        private static object SetMouseButton(int button, bool pressed)
        {
            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = pressed ? InputCommandType.MouseButtonDown : InputCommandType.MouseButtonUp,
                MouseButton = button
            });

            string[] names = { "left", "right", "middle" };
            return new SuccessResponse($"Mouse {names[button]} button {(pressed ? "pressed" : "released")}.", new
            {
                button,
                button_name = names[button],
                state = pressed ? "down" : "up"
            });
        }

        public static object MouseScroll(float delta)
        {
            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.MouseScroll,
                ScrollDelta = delta
            });

            return new SuccessResponse($"Mouse scrolled by {delta}.", new { scroll_delta = delta });
        }

        // --- Touch ---

        public static object Touch(int fingerId, string phase, Vector2 position)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("New Input System not available.");

            var touchscreen = _touchscreenCurrentProp?.GetValue(null);
            if (touchscreen == null)
                return new ErrorResponse("No touchscreen device is currently active. The game may not have a touchscreen configured.");

            try
            {
                // Access touchscreen.touches[fingerId]
                var touchesProp = _touchscreenType.GetProperty("touches");
                if (touchesProp == null)
                    return new ErrorResponse("Could not find touchscreen.touches property.");

                var touches = touchesProp.GetValue(touchscreen);
                // ReadOnlyArray<TouchControl> has an indexer
                var touchesType = touches.GetType();
                var indexer = touchesType.GetProperties()
                    .FirstOrDefault(pi => pi.GetIndexParameters().Length == 1);

                if (indexer == null)
                    return new ErrorResponse("Could not find touches indexer.");

                var touchControl = indexer.GetValue(touches, new object[] { fingerId });
                if (touchControl == null)
                    return new ErrorResponse($"Could not get touch control for finger {fingerId}.");

                // Set position
                var touchPosProperty = touchControl.GetType().GetProperty("position");
                if (touchPosProperty != null)
                {
                    var posControl = touchPosProperty.GetValue(touchControl);
                    if (_inputStateChangeMethod != null && posControl != null)
                    {
                        InvokeStateChange(posControl, position, typeof(Vector2));
                    }
                }

                // Set phase
                var touchPhaseProperty = touchControl.GetType().GetProperty("phase");
                if (touchPhaseProperty != null)
                {
                    var phaseControl = touchPhaseProperty.GetValue(touchControl);
                    var touchPhaseEnum = Type.GetType("UnityEngine.InputSystem.TouchPhase, Unity.InputSystem");
                    if (touchPhaseEnum != null && phaseControl != null)
                    {
                        // Map string phase to enum value
                        object phaseValue;
                        switch (phase.ToLowerInvariant())
                        {
                            case "began": case "begin": case "start":
                                phaseValue = Enum.Parse(touchPhaseEnum, "Began"); break;
                            case "moved": case "move":
                                phaseValue = Enum.Parse(touchPhaseEnum, "Moved"); break;
                            case "ended": case "end":
                                phaseValue = Enum.Parse(touchPhaseEnum, "Ended"); break;
                            case "canceled": case "cancelled": case "cancel":
                                phaseValue = Enum.Parse(touchPhaseEnum, "Canceled"); break;
                            case "stationary": case "none":
                                phaseValue = Enum.Parse(touchPhaseEnum, "None"); break;
                            default:
                                return new ErrorResponse($"Unknown touch phase '{phase}'. Use: began, moved, ended, canceled.");
                        }

                        // Use the underlying integer type for InputState.Change
                        var enumUnderlyingType = Enum.GetUnderlyingType(touchPhaseEnum);
                        InvokeStateChange(phaseControl, Convert.ChangeType(phaseValue, enumUnderlyingType), enumUnderlyingType);
                    }
                }

                return new SuccessResponse($"Touch finger {fingerId} phase '{phase}' at ({position.x}, {position.y}).", new
                {
                    finger_id = fingerId,
                    phase,
                    position = new[] { position.x, position.y }
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to simulate touch: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // --- Gamepad ---

        public static object GamepadButton(string buttonName, bool pressed)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("New Input System not available.");

            var gamepad = _gamepadCurrentProp?.GetValue(null);
            if (gamepad == null)
                return new ErrorResponse("No gamepad device is currently active.");

            try
            {
                // Resolve button name to property
                string propName = buttonName;
                if (_gamepadButtonMap.TryGetValue(buttonName, out var mapped))
                    propName = mapped;

                // Handle nested paths like "dpad/up"
                object control = gamepad;
                foreach (var segment in propName.Split('/'))
                {
                    var prop = control.GetType().GetProperty(segment,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (prop == null)
                        return new ErrorResponse($"Could not find gamepad control '{segment}' in path '{propName}'. " +
                                                 $"Available buttons: {string.Join(", ", _gamepadButtonMap.Keys)}");
                    control = prop.GetValue(control);
                }

                float value = pressed ? 1.0f : 0.0f;
                if (_inputStateChangeMethod != null)
                {
                    InvokeStateChange(control, value, typeof(float));
                }

                return new SuccessResponse($"Gamepad button '{buttonName}' {(pressed ? "pressed" : "released")}.", new
                {
                    button = buttonName,
                    state = pressed ? "down" : "up"
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to set gamepad button: {e.InnerException?.Message ?? e.Message}");
            }
        }

        public static object GamepadAxis(string axisName, JToken valueToken)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("New Input System not available.");

            var gamepad = _gamepadCurrentProp?.GetValue(null);
            if (gamepad == null)
                return new ErrorResponse("No gamepad device is currently active.");

            try
            {
                // Map axis names to gamepad properties
                var axisMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "leftstick", "leftStick" }, { "left_stick", "leftStick" },
                    { "rightstick", "rightStick" }, { "right_stick", "rightStick" },
                    { "lefttrigger", "leftTrigger" }, { "left_trigger", "leftTrigger" },
                    { "righttrigger", "rightTrigger" }, { "right_trigger", "rightTrigger" },
                };

                string propName = axisName;
                if (axisMap.TryGetValue(axisName, out var mappedAxis))
                    propName = mappedAxis;

                var axisProp = _gamepadType.GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (axisProp == null)
                    return new ErrorResponse($"Unknown gamepad axis '{axisName}'. Available: leftStick, rightStick, leftTrigger, rightTrigger");

                var axisControl = axisProp.GetValue(gamepad);

                // Determine value type: Vector2 for sticks, float for triggers
                if (propName.Contains("Stick") || propName.Contains("stick"))
                {
                    Vector2 vec;
                    if (valueToken.Type == JTokenType.Array)
                    {
                        var arr = valueToken.ToObject<float[]>();
                        if (arr == null || arr.Length < 2)
                            return new ErrorResponse("Stick axis_value must be [x, y].");
                        vec = new Vector2(arr[0], arr[1]);
                    }
                    else
                    {
                        return new ErrorResponse("Stick axis_value must be [x, y] array.");
                    }

                    if (_inputStateChangeMethod != null)
                    {
                        InvokeStateChange(axisControl, vec, typeof(Vector2));
                    }

                    return new SuccessResponse($"Gamepad {propName} set to ({vec.x}, {vec.y}).", new
                    {
                        axis = propName,
                        value = new[] { vec.x, vec.y }
                    });
                }
                else
                {
                    float val = valueToken.ToObject<float>();
                    if (_inputStateChangeMethod != null)
                    {
                        InvokeStateChange(axisControl, val, typeof(float));
                    }

                    return new SuccessResponse($"Gamepad {propName} set to {val}.", new
                    {
                        axis = propName,
                        value = val
                    });
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to set gamepad axis: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // --- Input State Query ---

        public static object GetInputState()
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("New Input System not available. Cannot read input state.");

            try
            {
                var result = new Dictionary<string, object>();

                // Keyboard state
                var keyboard = _keyboardCurrentProp?.GetValue(null);
                if (keyboard != null)
                {
                    var anyKeyProp = _keyboardType.GetProperty("anyKey");
                    var anyKeyControl = anyKeyProp?.GetValue(keyboard);
                    bool anyKeyPressed = false;
                    if (anyKeyControl != null)
                    {
                        var isPressedProp = anyKeyControl.GetType().GetProperty("isPressed");
                        if (isPressedProp != null)
                            anyKeyPressed = (bool)isPressedProp.GetValue(anyKeyControl);
                    }
                    result["keyboard"] = new { active = true, any_key_pressed = anyKeyPressed };
                }
                else
                {
                    result["keyboard"] = new { active = false };
                }

                // Mouse state
                var mouse = _mouseCurrentProp?.GetValue(null);
                if (mouse != null)
                {
                    var positionProp = _mouseType.GetProperty("position");
                    var posControl = positionProp?.GetValue(mouse);
                    Vector2 mousePos = Vector2.zero;
                    if (posControl != null)
                    {
                        var readValueMethod = posControl.GetType().GetMethod("ReadValue", Type.EmptyTypes);
                        if (readValueMethod != null)
                            mousePos = (Vector2)readValueMethod.Invoke(posControl, null);
                    }
                    result["mouse"] = new
                    {
                        active = true,
                        position = new[] { mousePos.x, mousePos.y }
                    };
                }
                else
                {
                    result["mouse"] = new { active = false };
                }

                // Gamepad state
                var gamepad = _gamepadCurrentProp?.GetValue(null);
                result["gamepad"] = new { active = gamepad != null };

                // Touchscreen state
                var touchscreen = _touchscreenCurrentProp?.GetValue(null);
                result["touchscreen"] = new { active = touchscreen != null };

                return new SuccessResponse("Current input device states.", result);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to read input state: {e.Message}");
            }
        }
    }
}
