using System.Collections.Concurrent;
using UnityEngine;

namespace MCPForUnity.Runtime.Input
{
    /// <summary>
    /// Runtime MonoBehaviour that processes input commands queued by the Editor-side MCP tools.
    /// Auto-injected into the scene when Play Mode starts (if legacy input fallback is needed).
    /// Hidden from the Hierarchy and not saved with the scene.
    /// </summary>
    public class MCPInputBridge : MonoBehaviour
    {
        /// <summary>
        /// Thread-safe command queue. Editor tools enqueue commands; this bridge dequeues and processes them in Update().
        /// Static so it survives domain reload if "Reload Domain" is disabled.
        /// </summary>
        public static readonly ConcurrentQueue<InputCommand> CommandQueue = new ConcurrentQueue<InputCommand>();

        /// <summary>Current virtual key states (for legacy input tracking).</summary>
        private static readonly ConcurrentDictionary<KeyCode, bool> _keyStates = new ConcurrentDictionary<KeyCode, bool>();

        /// <summary>Current virtual mouse position.</summary>
        public static Vector2 VirtualMousePosition { get; private set; }

        /// <summary>Current virtual mouse button states.</summary>
        private static readonly bool[] _mouseButtonStates = new bool[3];

        public static bool IsActive { get; private set; }

        private void OnEnable()
        {
            IsActive = true;
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

        private void Update()
        {
            int processed = 0;
            while (CommandQueue.TryDequeue(out var cmd) && processed < 100)
            {
                ProcessCommand(cmd);
                processed++;
            }
        }

        private void ProcessCommand(InputCommand cmd)
        {
            switch (cmd.Type)
            {
                case InputCommandType.KeyDown:
                    _keyStates[cmd.KeyCode] = true;
                    break;

                case InputCommandType.KeyUp:
                    _keyStates[cmd.KeyCode] = false;
                    break;

                case InputCommandType.MouseMove:
                    VirtualMousePosition = cmd.Position;
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
                    // Legacy Input doesn't support scroll injection
                    break;

                case InputCommandType.TouchBegin:
                case InputCommandType.TouchMove:
                case InputCommandType.TouchEnd:
                    ProcessTouch(cmd);
                    break;
            }
        }

        private static System.Reflection.MethodInfo _simulateTouchMethod;
        private static bool _simulateTouchResolved;

        private void ProcessTouch(InputCommand cmd)
        {
            // Use reflection to call Input.SimulateTouch — it exists in some Unity versions
            // but was removed in Unity 6. Reflection avoids compile errors on any version.
            try
            {
                if (!_simulateTouchResolved)
                {
                    _simulateTouchResolved = true;
                    _simulateTouchMethod = typeof(UnityEngine.Input).GetMethod(
                        "SimulateTouch",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null,
                        new System.Type[] { typeof(UnityEngine.Touch) },
                        null);
                }

                if (_simulateTouchMethod == null)
                    return; // SimulateTouch not available in this Unity version

                var touch = new UnityEngine.Touch
                {
                    fingerId = cmd.TouchFingerId,
                    position = cmd.Position,
                    rawPosition = cmd.Position,
                    deltaPosition = Vector2.zero,
                    deltaTime = Time.deltaTime,
                    tapCount = 1,
                    type = TouchType.Direct
                };

                switch (cmd.Type)
                {
                    case InputCommandType.TouchBegin:
                        touch.phase = TouchPhase.Began;
                        break;
                    case InputCommandType.TouchMove:
                        touch.phase = TouchPhase.Moved;
                        break;
                    case InputCommandType.TouchEnd:
                        touch.phase = TouchPhase.Ended;
                        break;
                }

                _simulateTouchMethod.Invoke(null, new object[] { touch });
            }
            catch (System.Exception)
            {
                // Silently fail — touch simulation via legacy Input is best-effort
            }
        }

        /// <summary>Check if a virtual key is currently "pressed" in the bridge.</summary>
        public static bool IsKeyDown(KeyCode key)
        {
            return _keyStates.TryGetValue(key, out var pressed) && pressed;
        }

        /// <summary>Check if a virtual mouse button is currently "pressed" in the bridge.</summary>
        public static bool IsMouseButtonDown(int button)
        {
            return button >= 0 && button < 3 && _mouseButtonStates[button];
        }

        public static void ClearState()
        {
            _keyStates.Clear();
            _mouseButtonStates[0] = false;
            _mouseButtonStates[1] = false;
            _mouseButtonStates[2] = false;
            VirtualMousePosition = Vector2.zero;
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
