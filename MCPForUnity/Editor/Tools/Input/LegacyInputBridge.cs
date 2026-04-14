using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Input;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    /// <summary>
    /// Editor-side controller for the MCPInputBridge runtime component.
    /// Manages bridge lifecycle (create on Play Mode enter, auto-destroyed on exit).
    /// Provides static methods for queuing input commands to the bridge.
    /// </summary>
    [InitializeOnLoad]
    internal static class LegacyInputBridge
    {
        // Key name to KeyCode mapping for legacy input
        private static Dictionary<string, KeyCode> _keyCodeMap;
        private static bool _keyCodeMapBuilt;

        static LegacyInputBridge()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static bool IsBridgeActive => MCPInputBridge.IsActive;

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // Only inject bridge if New Input System is NOT available (legacy fallback)
                if (!InputSimulator.IsAvailable)
                    EnsureBridgeExists();
            }
        }

        private static void EnsureBridgeExists()
        {
            if (MCPInputBridge.IsActive)
                return;

            var go = new GameObject("[MCP Input Bridge]");
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MCPInputBridge>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        private static void EnsureKeyCodeMap()
        {
            if (_keyCodeMapBuilt) return;
            _keyCodeMapBuilt = true;

            _keyCodeMap = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
            {
                string name = kc.ToString();
                _keyCodeMap[name] = kc;
            }

            // Common aliases
            _keyCodeMap["ctrl"] = KeyCode.LeftControl;
            _keyCodeMap["control"] = KeyCode.LeftControl;
            _keyCodeMap["shift"] = KeyCode.LeftShift;
            _keyCodeMap["alt"] = KeyCode.LeftAlt;
            _keyCodeMap["enter"] = KeyCode.Return;
            _keyCodeMap["esc"] = KeyCode.Escape;
            _keyCodeMap["del"] = KeyCode.Delete;
            _keyCodeMap["ins"] = KeyCode.Insert;
            _keyCodeMap["up"] = KeyCode.UpArrow;
            _keyCodeMap["down"] = KeyCode.DownArrow;
            _keyCodeMap["left"] = KeyCode.LeftArrow;
            _keyCodeMap["right"] = KeyCode.RightArrow;

            // Single-char letter/digit shortcuts
            for (char c = 'a'; c <= 'z'; c++)
                _keyCodeMap[c.ToString()] = (KeyCode)c;
            for (char c = '0'; c <= '9'; c++)
                _keyCodeMap[c.ToString()] = (KeyCode)((int)KeyCode.Alpha0 + (c - '0'));
        }

        private static bool TryGetKeyCode(string keyName, out KeyCode keyCode)
        {
            EnsureKeyCodeMap();
            return _keyCodeMap.TryGetValue(keyName, out keyCode);
        }

        // --- Public API for ManageInput to call ---

        public static object KeyDown(string keyName)
        {
            if (!TryGetKeyCode(keyName, out var keyCode))
                return new ErrorResponse($"Unknown key '{keyName}' for legacy input. Use Unity KeyCode names (e.g., 'W', 'Space', 'LeftShift').");

            EnsureBridgeInPlayMode();

            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.KeyDown,
                KeyCode = keyCode
            });

            return new SuccessResponse($"Key '{keyName}' pressed (legacy bridge).", new
            {
                key = keyName,
                key_code = keyCode.ToString(),
                state = "down",
                note = "Legacy Input: Games using Input.GetKey() may not detect this. Consider New Input System for full support."
            });
        }

        public static object KeyUp(string keyName)
        {
            if (!TryGetKeyCode(keyName, out var keyCode))
                return new ErrorResponse($"Unknown key '{keyName}'.");

            EnsureBridgeInPlayMode();

            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.KeyUp,
                KeyCode = keyCode
            });

            return new SuccessResponse($"Key '{keyName}' released (legacy bridge).", new
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

            double targetTime = EditorApplication.timeSinceStartup + duration;
            var capturedKey = keyName;

            void CheckAndRelease()
            {
                if (EditorApplication.timeSinceStartup >= targetTime)
                    KeyUp(capturedKey);
                else
                    EditorApplication.delayCall += CheckAndRelease;
            }

            EditorApplication.delayCall += CheckAndRelease;

            return new SuccessResponse($"Key '{keyName}' pressed (legacy bridge). Will release after {duration}s.");
        }

        public static object MouseMove(Vector2 position)
        {
            EnsureBridgeInPlayMode();

            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.MouseMove,
                Position = position
            });

            return new SuccessResponse($"Mouse moved to ({position.x}, {position.y}) (legacy bridge).", new
            {
                position = new[] { position.x, position.y },
                note = "Legacy Input: Input.mousePosition is read-only hardware state. The bridge tracks virtual position only."
            });
        }

        public static object MouseButtonDown(int button)
        {
            EnsureBridgeInPlayMode();

            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.MouseButtonDown,
                MouseButton = button
            });

            string[] names = { "left", "right", "middle" };
            return new SuccessResponse($"Mouse {names[button]} button pressed (legacy bridge).", new
            {
                button,
                state = "down"
            });
        }

        public static object MouseButtonUp(int button)
        {
            EnsureBridgeInPlayMode();

            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.MouseButtonUp,
                MouseButton = button
            });

            string[] names = { "left", "right", "middle" };
            return new SuccessResponse($"Mouse {names[button]} button released (legacy bridge).", new
            {
                button,
                state = "up"
            });
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
                    MouseButtonUp(capturedButton);
                else
                    EditorApplication.delayCall += CheckAndRelease;
            }

            EditorApplication.delayCall += CheckAndRelease;

            return new SuccessResponse($"Mouse button {button} clicked (legacy bridge).");
        }

        public static object MouseScroll(float delta)
        {
            EnsureBridgeInPlayMode();

            MCPInputBridge.CommandQueue.Enqueue(new InputCommand
            {
                Type = InputCommandType.MouseScroll,
                ScrollDelta = delta
            });

            return new SuccessResponse($"Mouse scrolled by {delta} (legacy bridge).", new
            {
                scroll_delta = delta,
                note = "Legacy Input does not support scroll injection. This is tracked as virtual state only."
            });
        }

        private static void EnsureBridgeInPlayMode()
        {
            if (EditorApplication.isPlaying && !MCPInputBridge.IsActive)
                EnsureBridgeExists();
        }

        [InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            _keyCodeMapBuilt = false;
        }
    }
}
