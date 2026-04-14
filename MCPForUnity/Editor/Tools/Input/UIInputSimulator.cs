using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MCPForUnity.Editor.Tools.Input
{
    /// <summary>
    /// Simulates UI interactions via Unity's EventSystem.
    /// Works with any input system (legacy or new) since it operates at the EventSystem level.
    /// </summary>
    internal static class UIInputSimulator
    {
        public static object ClickUI(ToolParams p)
        {
            var nameResult = p.GetRequired("element_name");
            if (!nameResult.IsSuccess)
                return new ErrorResponse(nameResult.ErrorMessage);

            string elementName = nameResult.Value;

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
                return new ErrorResponse("No EventSystem found in scene. Add an EventSystem to enable UI interaction.");

            // Find the target UI element
            GameObject target = FindUIElement(elementName);
            if (target == null)
                return new ErrorResponse(
                    $"UI element '{elementName}' not found. Provide a GameObject name or hierarchy path (e.g., 'Canvas/Panel/Button').");

            // Verify it can receive pointer events
            if (!CanReceivePointerEvents(target))
                return new ErrorResponse(
                    $"UI element '{elementName}' found but cannot receive pointer events. " +
                    "Ensure it has a Graphic component with raycastTarget=true, or implements IPointerClickHandler.");

            try
            {
                // Get the element's screen position for the pointer event data
                RectTransform rectTransform = target.GetComponent<RectTransform>();
                Vector2 screenPoint = Vector2.zero;

                if (rectTransform != null)
                {
                    // Get the center of the UI element in screen coordinates
                    Canvas canvas = target.GetComponentInParent<Canvas>();
                    if (canvas != null)
                    {
                        Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                        Vector3[] corners = new Vector3[4];
                        rectTransform.GetWorldCorners(corners);
                        Vector3 center = (corners[0] + corners[2]) / 2f;

                        if (cam != null)
                            screenPoint = cam.WorldToScreenPoint(center);
                        else
                            screenPoint = center; // ScreenSpaceOverlay
                    }
                }

                // Create pointer event data
                var pointerData = new PointerEventData(eventSystem)
                {
                    position = screenPoint,
                    button = PointerEventData.InputButton.Left,
                    clickCount = 1
                };

                // Set the pointer enter target for proper event flow
                pointerData.pointerEnter = target;
                pointerData.pointerPress = target;

                // Execute the event chain: Enter → Down → Up → Click
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerClickHandler);

                // Also try submit handler (for buttons that use Submit instead of Click)
                ExecuteEvents.Execute(target, new BaseEventData(eventSystem), ExecuteEvents.submitHandler);

                return new SuccessResponse($"Clicked UI element '{elementName}'.", new
                {
                    element = elementName,
                    game_object = target.name,
                    screen_position = new[] { screenPoint.x, screenPoint.y }
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to click UI element '{elementName}': {e.Message}");
            }
        }

        private static GameObject FindUIElement(string elementName)
        {
            // Try direct find first (full path or name)
            var go = GameObject.Find(elementName);
            if (go != null)
                return go;

            // Search all canvases for the element by name
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (var canvas in canvases)
            {
                var found = FindChildRecursive(canvas.transform, elementName);
                if (found != null)
                    return found.gameObject;
            }

            return null;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;

                var found = FindChildRecursive(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static bool CanReceivePointerEvents(GameObject go)
        {
            // Check for any IPointerClickHandler or IPointerDownHandler
            var clickHandlers = go.GetComponents<IPointerClickHandler>();
            if (clickHandlers.Length > 0)
                return true;

            var downHandlers = go.GetComponents<IPointerDownHandler>();
            if (downHandlers.Length > 0)
                return true;

            var submitHandlers = go.GetComponents<ISubmitHandler>();
            if (submitHandlers.Length > 0)
                return true;

            // Check for Selectable (Button, Toggle, etc.)
            var selectable = go.GetComponent<UnityEngine.UI.Selectable>();
            if (selectable != null && selectable.interactable)
                return true;

            // Check for a Graphic with raycastTarget (can receive events through parent handlers)
            var graphic = go.GetComponent<UnityEngine.UI.Graphic>();
            if (graphic != null && graphic.raycastTarget)
                return true;

            return false;
        }
    }
}
