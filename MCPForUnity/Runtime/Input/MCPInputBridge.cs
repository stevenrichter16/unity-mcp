using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MCPForUnity.Runtime.Input
{
    /// <summary>
    /// Runtime MonoBehaviour that processes input commands and continuously re-applies
    /// virtual key/mouse state every frame via the New Input System's QueueStateEvent.
    /// This ensures simulated input persists across frames (QueueStateEvent is single-frame
    /// by design — the bridge re-queues each frame to maintain held state).
    /// Auto-injected into the scene when Play Mode starts.
    /// </summary>
    public class MCPInputBridge : MonoBehaviour
    {
        public static readonly ConcurrentQueue<InputCommand> CommandQueue = new ConcurrentQueue<InputCommand>();

        // Persistent virtual state — survives across frames
        private static readonly ConcurrentDictionary<int, bool> _keyStates = new ConcurrentDictionary<int, bool>();
        private static readonly bool[] _mouseButtonStates = new bool[3];
        private static Vector2 _mousePosition;
        private static bool _mousePositionSet;
        private static Vector2 _scrollDelta;
        private static bool _scrollPending;

        // New Input System types resolved via reflection
        private static System.Type _keyboardType;
        private static System.Type _mouseType;
        private static System.Type _inputSystemType;
        private static System.Type _keyboardStateType;
        private static System.Type _mouseStateType;
        private static System.Type _keyEnum;
        private static System.Type _mouseButtonEnum;
        private static MethodInfo _queueStateEventMethod;
        private static MethodInfo _keyboardStateSetMethod;
        private static MethodInfo _mouseStateWithButtonMethod;
        private static PropertyInfo _keyboardCurrentProp;
        private static PropertyInfo _mouseCurrentProp;
        private static FieldInfo _mousePositionField;
        private static FieldInfo _mouseScrollField;
        private static bool _typesResolved;
        private static bool _newInputSystemAvailable;

        public static bool IsActive { get; private set; }

        private void OnEnable()
        {
            IsActive = true;
            ResolveTypes();
            ConfigureInputSettings();
        }

        private void OnDisable()
        {
            IsActive = false;
        }

        private void OnDestroy()
        {
            IsActive = false;
            ClearState();
        }

        private static void ResolveTypes()
        {
            if (_typesResolved) return;
            _typesResolved = true;

            _inputSystemType = System.Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
            _keyboardType = System.Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            _mouseType = System.Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
            _keyboardStateType = System.Type.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState, Unity.InputSystem");
            _mouseStateType = System.Type.GetType("UnityEngine.InputSystem.LowLevel.MouseState, Unity.InputSystem");
            _keyEnum = System.Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            _mouseButtonEnum = System.Type.GetType("UnityEngine.InputSystem.LowLevel.MouseButton, Unity.InputSystem");

            if (_inputSystemType == null) return;

            _keyboardCurrentProp = _keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _mouseCurrentProp = _mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

            if (_keyboardStateType != null && _keyEnum != null)
                _keyboardStateSetMethod = _keyboardStateType.GetMethod("Set", new[] { _keyEnum, typeof(bool) });

            if (_mouseStateType != null)
            {
                _mousePositionField = _mouseStateType.GetField("position");
                _mouseScrollField = _mouseStateType.GetField("scroll");
                if (_mouseButtonEnum != null)
                    _mouseStateWithButtonMethod = _mouseStateType.GetMethod("WithButton", new[] { _mouseButtonEnum, typeof(bool) });
            }

            // Find QueueStateEvent generic method
            var queueMethods = _inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "QueueStateEvent" && m.IsGenericMethod);
            foreach (var method in queueMethods)
            {
                var p = method.GetParameters();
                if (p.Length >= 2 && !p[1].ParameterType.IsByRef)
                {
                    _queueStateEventMethod = method;
                    break;
                }
            }

            _newInputSystemAvailable = _queueStateEventMethod != null
                && _keyboardStateType != null
                && _keyboardStateSetMethod != null;
        }

        /// <summary>
        /// Configure Input System to accept input even when Game View isn't focused.
        /// </summary>
        private static void ConfigureInputSettings()
        {
            if (_inputSystemType == null) return;
            try
            {
                var settingsProp = _inputSystemType.GetProperty("settings", BindingFlags.Public | BindingFlags.Static);
                if (settingsProp == null) return;
                var settings = settingsProp.GetValue(null);
                if (settings == null) return;

                // Set editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView (2)
                var editorBehaviorProp = settings.GetType().GetProperty("editorInputBehaviorInPlayMode");
                if (editorBehaviorProp != null)
                {
                    var enumType = editorBehaviorProp.PropertyType;
                    var allDevices = System.Enum.Parse(enumType, "AllDeviceInputAlwaysGoesToGameView");
                    editorBehaviorProp.SetValue(settings, allDevices);
                }

                // Set backgroundBehavior = IgnoreFocus (1)
                var bgBehaviorProp = settings.GetType().GetProperty("backgroundBehavior");
                if (bgBehaviorProp != null)
                {
                    var enumType = bgBehaviorProp.PropertyType;
                    var ignoreFocus = System.Enum.Parse(enumType, "IgnoreFocus");
                    bgBehaviorProp.SetValue(settings, ignoreFocus);
                }
            }
            catch (System.Exception) { }
        }

        private void Update()
        {
            // 1. Process new commands from the queue
            int processed = 0;
            while (CommandQueue.TryDequeue(out var cmd) && processed < 100)
            {
                ProcessCommand(cmd);
                processed++;
            }

            // 2. Re-apply persistent state every frame via QueueStateEvent
            if (_newInputSystemAvailable)
            {
                ReApplyKeyboardState();
                ReApplyMouseState();
            }
        }

        private void ProcessCommand(InputCommand cmd)
        {
            switch (cmd.Type)
            {
                case InputCommandType.KeyDown:
                    _keyStates[(int)cmd.KeyCode] = true;
                    break;
                case InputCommandType.KeyUp:
                    _keyStates[(int)cmd.KeyCode] = false;
                    break;
                case InputCommandType.MouseMove:
                    _mousePosition = cmd.Position;
                    _mousePositionSet = true;
                    break;
                case InputCommandType.MouseButtonDown:
                    if (cmd.MouseButton >= 0 && cmd.MouseButton < 3)
                        _mouseButtonStates[cmd.MouseButton] = true;
                    break;
                case InputCommandType.MouseButtonUp:
                    if (cmd.MouseButton >= 0 && cmd.MouseButton < 3)
                        _mouseButtonStates[cmd.MouseButton] = false;
                    break;
                case InputCommandType.MouseScroll:
                    _scrollDelta = new Vector2(0, cmd.ScrollDelta * 120f);
                    _scrollPending = true;
                    break;
            }
        }

        private void ReApplyKeyboardState()
        {
            // Only re-queue if any keys are held
            bool anyPressed = false;
            foreach (var kvp in _keyStates)
                if (kvp.Value) { anyPressed = true; break; }

            if (!anyPressed) return;

            var keyboard = _keyboardCurrentProp?.GetValue(null);
            if (keyboard == null) return;

            try
            {
                var state = System.Activator.CreateInstance(_keyboardStateType);

                foreach (var kvp in _keyStates)
                {
                    if (!kvp.Value) continue;
                    // Map KeyCode int to Key enum
                    var keyEnumValue = KeyCodeToInputSystemKey(kvp.Key);
                    if (keyEnumValue != null)
                        _keyboardStateSetMethod.Invoke(state, new[] { keyEnumValue, (object)true });
                }

                var genericQueue = _queueStateEventMethod.MakeGenericMethod(_keyboardStateType);
                genericQueue.Invoke(null, new[] { keyboard, state, (object)(-1.0) });
            }
            catch (System.Exception) { }
        }

        private void ReApplyMouseState()
        {
            bool anyButton = _mouseButtonStates[0] || _mouseButtonStates[1] || _mouseButtonStates[2];
            if (!anyButton && !_mousePositionSet && !_scrollPending) return;

            var mouse = _mouseCurrentProp?.GetValue(null);
            if (mouse == null) return;

            try
            {
                var state = System.Activator.CreateInstance(_mouseStateType);

                if (_mousePositionSet && _mousePositionField != null)
                    _mousePositionField.SetValue(state, _mousePosition);

                if (_scrollPending && _mouseScrollField != null)
                {
                    _mouseScrollField.SetValue(state, _scrollDelta);
                    _scrollPending = false; // scroll is one-shot
                }

                if (_mouseStateWithButtonMethod != null)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        if (_mouseButtonStates[i])
                        {
                            var mbValue = System.Enum.ToObject(_mouseButtonEnum, i);
                            state = _mouseStateWithButtonMethod.Invoke(state, new[] { mbValue, (object)true });
                        }
                    }
                }

                var genericQueue = _queueStateEventMethod.MakeGenericMethod(_mouseStateType);
                genericQueue.Invoke(null, new[] { mouse, state, (object)(-1.0) });
            }
            catch (System.Exception) { }
        }

        // --- KeyCode to Input System Key mapping ---

        private static Dictionary<int, object> _keyCodeToKeyMap;

        private static object KeyCodeToInputSystemKey(int keyCodeInt)
        {
            if (_keyCodeToKeyMap == null)
                BuildKeyCodeMap();
            _keyCodeToKeyMap.TryGetValue(keyCodeInt, out var result);
            return result;
        }

        private static void BuildKeyCodeMap()
        {
            _keyCodeToKeyMap = new Dictionary<int, object>();
            if (_keyEnum == null) return;

            // Build reverse map from Key enum names
            var keyValues = new Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var val in System.Enum.GetValues(_keyEnum))
                keyValues[System.Enum.GetName(_keyEnum, val)] = val;

            void Map(KeyCode kc, string keyName)
            {
                if (keyValues.TryGetValue(keyName, out var v))
                    _keyCodeToKeyMap[(int)kc] = v;
            }

            // Letters
            for (char c = 'a'; c <= 'z'; c++)
                Map((KeyCode)c, c.ToString().ToUpper());

            // Digits
            for (int i = 0; i <= 9; i++)
                Map(KeyCode.Alpha0 + i, $"Digit{i}");

            // Numpad
            for (int i = 0; i <= 9; i++)
                Map(KeyCode.Keypad0 + i, $"Numpad{i}");

            // Arrows
            Map(KeyCode.UpArrow, "UpArrow");
            Map(KeyCode.DownArrow, "DownArrow");
            Map(KeyCode.LeftArrow, "LeftArrow");
            Map(KeyCode.RightArrow, "RightArrow");

            // Function keys
            for (int i = 1; i <= 12; i++)
                Map(KeyCode.F1 + (i - 1), $"F{i}");

            // Special
            Map(KeyCode.Space, "Space");
            Map(KeyCode.Return, "Enter");
            Map(KeyCode.Escape, "Escape");
            Map(KeyCode.Tab, "Tab");
            Map(KeyCode.Backspace, "Backspace");
            Map(KeyCode.Delete, "Delete");
            Map(KeyCode.LeftShift, "LeftShift");
            Map(KeyCode.RightShift, "RightShift");
            Map(KeyCode.LeftControl, "LeftCtrl");
            Map(KeyCode.RightControl, "RightCtrl");
            Map(KeyCode.LeftAlt, "LeftAlt");
            Map(KeyCode.RightAlt, "RightAlt");
            Map(KeyCode.Period, "Period");
            Map(KeyCode.Comma, "Comma");
            Map(KeyCode.Semicolon, "Semicolon");
            Map(KeyCode.Slash, "Slash");
            Map(KeyCode.Minus, "Minus");
            Map(KeyCode.Equals, "Equals");
            Map(KeyCode.LeftBracket, "LeftBracket");
            Map(KeyCode.RightBracket, "RightBracket");
            Map(KeyCode.Insert, "Insert");
            Map(KeyCode.Home, "Home");
            Map(KeyCode.End, "End");
            Map(KeyCode.PageUp, "PageUp");
            Map(KeyCode.PageDown, "PageDown");
        }

        // --- Public API ---

        public static bool IsKeyHeld(KeyCode key)
        {
            return _keyStates.TryGetValue((int)key, out var pressed) && pressed;
        }

        public static bool IsMouseButtonHeld(int button)
        {
            return button >= 0 && button < 3 && _mouseButtonStates[button];
        }

        public static void ClearState()
        {
            _keyStates.Clear();
            _mouseButtonStates[0] = false;
            _mouseButtonStates[1] = false;
            _mouseButtonStates[2] = false;
            _mousePosition = Vector2.zero;
            _mousePositionSet = false;
            _scrollDelta = Vector2.zero;
            _scrollPending = false;
            while (CommandQueue.TryDequeue(out _)) { }
        }
    }

    public enum InputCommandType
    {
        KeyDown,
        KeyUp,
        MouseMove,
        MouseButtonDown,
        MouseButtonUp,
        MouseScroll,
        TouchBegin,
        TouchMove,
        TouchEnd
    }

    public struct InputCommand
    {
        public InputCommandType Type;
        public KeyCode KeyCode;
        public int MouseButton;
        public Vector2 Position;
        public float ScrollDelta;
        public int TouchFingerId;
    }
}
