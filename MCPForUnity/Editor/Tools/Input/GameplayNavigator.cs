using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    /// <summary>
    /// MCP-only gameplay navigation helpers: pathfinding, surroundings query, and turn waiting.
    /// These are used exclusively by the MCP manage_input tool to let AI assistants navigate
    /// and observe the game world programmatically. Real players use normal keyboard input —
    /// this pathfinding is never used during normal gameplay.
    ///
    /// All game types are accessed via reflection to keep the MCP package game-agnostic.
    /// </summary>
    internal static class GameplayNavigator
    {
        // Cached reflection handles — resolved once, reused
        private static bool _resolved;
        private static bool _available;

        // InputHandler
        private static Type _inputHandlerType;
        private static PropertyInfo _playerEntityProp;
        private static PropertyInfo _currentZoneProp;
        private static PropertyInfo _turnManagerProp;
        private static PropertyInfo _zoneRendererProp;

        // Zone
        private static Type _zoneType;
        private static MethodInfo _getCellMethod;
        private static MethodInfo _getEntityPositionMethod;
        private static MethodInfo _inBoundsMethod;
        private static MethodInfo _forEachCellMethod;
        private static FieldInfo _zoneWidthField;
        private static FieldInfo _zoneHeightField;

        // Cell
        private static Type _cellType;
        private static MethodInfo _isSolidMethod;
        private static FieldInfo _cellObjectsField;
        private static PropertyInfo _cellXProp;
        private static PropertyInfo _cellYProp;

        // Entity
        private static Type _entityType;
        private static MethodInfo _hasTagMethod;
        private static MethodInfo _getTagMethod;
        private static PropertyInfo _entityBlueprintNameProp;
        private static PropertyInfo _entityStatisticsProp;
        private static PropertyInfo _entityTagsProp;
        private static MethodInfo _getPartMethod;

        // RenderPart
        private static Type _renderPartType;
        private static FieldInfo _displayNameField;
        private static FieldInfo _renderStringField;

        // PhysicsPart
        private static Type _physicsPartType;
        private static PropertyInfo _takeableProp;
        private static FieldInfo _takeableField;

        // GrimoirePart
        private static Type _grimoirePartType;
        private static FieldInfo _mutationClassNameField;
        private static FieldInfo _knowledgePropertyField;

        // InventoryPart
        private static Type _inventoryPartType;
        private static PropertyInfo _invObjectsProp;

        // MovementSystem
        private static Type _movementSystemType;
        private static MethodInfo _tryMoveExMethod;

        // TurnManager
        private static Type _turnManagerType;
        private static MethodInfo _endTurnMethod;
        private static MethodInfo _processUntilPlayerTurnMethod;

        // ZoneRenderer
        private static Type _zoneRendererType;
        private static MethodInfo _markDirtyMethod;

        // FactionManager
        private static Type _factionManagerType;
        private static MethodInfo _isHostileMethod;
        private static PropertyInfo _factionManagerInstanceProp;

        [InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            _resolved = false;
            _available = false;
        }

        private static bool EnsureResolved()
        {
            if (_resolved) return _available;
            _resolved = true;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            // Find game types by name, preferring types from the game assembly
            Type FindType(string name)
            {
                Type result = null;
                foreach (var a in assemblies)
                {
                    Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.Name != name) continue;
                        // Prefer types from the game's namespace
                        if (t.Namespace != null && t.Namespace.StartsWith("CavesOfOoo"))
                            return t;
                        if (result == null)
                            result = t;
                    }
                }
                return result;
            }

            _inputHandlerType = FindType("InputHandler");
            if (_inputHandlerType == null) { _available = false; return false; }

            _playerEntityProp = _inputHandlerType.GetProperty("PlayerEntity");
            _currentZoneProp = _inputHandlerType.GetProperty("CurrentZone");
            _turnManagerProp = _inputHandlerType.GetProperty("TurnManager");
            _zoneRendererProp = _inputHandlerType.GetProperty("ZoneRenderer");

            _entityType = FindType("Entity");
            _zoneType = _currentZoneProp?.PropertyType;
            _turnManagerType = _turnManagerProp?.PropertyType;
            _zoneRendererType = _zoneRendererProp?.PropertyType;

            if (_zoneType != null)
            {
                _getCellMethod = _zoneType.GetMethod("GetCell", new[] { typeof(int), typeof(int) });
                _getEntityPositionMethod = _zoneType.GetMethod("GetEntityPosition");
                _inBoundsMethod = _zoneType.GetMethod("InBounds", new[] { typeof(int), typeof(int) });
                _forEachCellMethod = _zoneType.GetMethod("ForEachCell");
                _zoneWidthField = _zoneType.GetField("Width", BindingFlags.Public | BindingFlags.Static);
                _zoneHeightField = _zoneType.GetField("Height", BindingFlags.Public | BindingFlags.Static);
            }

            _cellType = _getCellMethod?.ReturnType;
            if (_cellType != null)
            {
                _isSolidMethod = _cellType.GetMethod("IsSolid");
                _cellObjectsField = _cellType.GetField("Objects");
                _cellXProp = _cellType.GetProperty("X");
                _cellYProp = _cellType.GetProperty("Y");
            }

            if (_entityType != null)
            {
                _hasTagMethod = _entityType.GetMethod("HasTag", new[] { typeof(string) });
                _getTagMethod = _entityType.GetMethod("GetTag", new[] { typeof(string), typeof(string) });
                _entityBlueprintNameProp = _entityType.GetProperty("BlueprintName")
                    ?? (PropertyInfo)null; // might be field
                _entityStatisticsProp = _entityType.GetProperty("Statistics");
                _entityTagsProp = _entityType.GetProperty("Tags");

                // BlueprintName and Statistics might be fields, not properties
                if (_entityBlueprintNameProp == null)
                    _entityBlueprintNameProp = null; // will use field fallback below

                _getPartMethod = _entityType.GetMethods().FirstOrDefault(m =>
                    m.Name == "GetPart" && m.IsGenericMethod && m.GetParameters().Length == 0);
            }

            _renderPartType = FindType("RenderPart");
            if (_renderPartType != null)
            {
                _displayNameField = _renderPartType.GetField("DisplayName");
                _renderStringField = _renderPartType.GetField("RenderString");
            }

            _physicsPartType = FindType("PhysicsPart");
            if (_physicsPartType != null)
            {
                _takeableProp = _physicsPartType.GetProperty("Takeable");
                _takeableField = _physicsPartType.GetField("Takeable");
            }

            _grimoirePartType = FindType("GrimoirePart");
            if (_grimoirePartType != null)
            {
                _mutationClassNameField = _grimoirePartType.GetField("MutationClassName");
                _knowledgePropertyField = _grimoirePartType.GetField("KnowledgeProperty");
            }

            _inventoryPartType = FindType("InventoryPart");
            if (_inventoryPartType != null)
                _invObjectsProp = _inventoryPartType.GetProperty("Objects");

            _movementSystemType = FindType("MovementSystem");
            if (_movementSystemType != null)
                _tryMoveExMethod = _movementSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "TryMoveEx");

            if (_turnManagerType != null)
            {
                _endTurnMethod = _turnManagerType.GetMethod("EndTurn");
                _processUntilPlayerTurnMethod = _turnManagerType.GetMethod("ProcessUntilPlayerTurn");
            }

            if (_zoneRendererType != null)
                _markDirtyMethod = _zoneRendererType.GetMethod("MarkDirty", new[] { typeof(string) });

            _factionManagerType = FindType("FactionManager");
            if (_factionManagerType != null)
            {
                _factionManagerInstanceProp = _factionManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _isHostileMethod = _factionManagerType.GetMethod("IsHostile");
            }

            _available = _inputHandlerType != null && _zoneType != null && _entityType != null;
            return _available;
        }

        // --- Reflection helpers ---

        private static object GetHandler()
        {
            return UnityEngine.Object.FindAnyObjectByType(_inputHandlerType);
        }

        private static object GetPart(object entity, Type partType)
        {
            if (_getPartMethod == null || entity == null || partType == null) return null;
            var generic = _getPartMethod.MakeGenericMethod(partType);
            return generic.Invoke(entity, null);
        }

        private static string GetDisplayName(object entity)
        {
            var rend = GetPart(entity, _renderPartType);
            if (rend != null)
            {
                var name = _displayNameField?.GetValue(rend)?.ToString();
                if (name != null) return name;
            }
            // Fallback to BlueprintName (field)
            return _entityType.GetField("BlueprintName")?.GetValue(entity)?.ToString();
        }

        private static string GetBlueprintName(object entity)
        {
            return _entityType.GetField("BlueprintName")?.GetValue(entity)?.ToString();
        }

        private static bool HasTag(object entity, string tag)
        {
            return (bool)(_hasTagMethod?.Invoke(entity, new object[] { tag }) ?? false);
        }

        private static string GetTag(object entity, string tag)
        {
            return _getTagMethod?.Invoke(entity, new object[] { tag, (string)null })?.ToString();
        }

        private static (int x, int y) GetEntityPosition(object zone, object entity)
        {
            var result = _getEntityPositionMethod.Invoke(zone, new[] { entity });
            // Returns ValueTuple<int, int>
            var tupleType = result.GetType();
            int x = (int)tupleType.GetField("Item1").GetValue(result);
            int y = (int)tupleType.GetField("Item2").GetValue(result);
            return (x, y);
        }

        private static object GetCell(object zone, int x, int y)
        {
            return _getCellMethod.Invoke(zone, new object[] { x, y });
        }

        private static bool IsCellSolid(object cell)
        {
            if (cell == null) return true;
            return (bool)_isSolidMethod.Invoke(cell, null);
        }

        private static bool InBounds(object zone, int x, int y)
        {
            return (bool)_inBoundsMethod.Invoke(zone, new object[] { x, y });
        }

        private static System.Collections.IList GetCellObjects(object cell)
        {
            return _cellObjectsField?.GetValue(cell) as System.Collections.IList;
        }

        private static int GetZoneWidth()
        {
            return (int)(_zoneWidthField?.GetValue(null) ?? 80);
        }

        private static int GetZoneHeight()
        {
            return (int)(_zoneHeightField?.GetValue(null) ?? 25);
        }

        // =====================================================================
        // move_to — MCP-only pathfinding for AI assistant navigation
        // This is NOT used during normal gameplay. Real players move with WASD.
        // =====================================================================

        public static object MoveTo(ToolParams p)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("Game reflection not available. Is the game running?");

            var handler = GetHandler();
            if (handler == null)
                return new ErrorResponse("InputHandler not found. Is the game bootstrapped?");

            var player = _playerEntityProp.GetValue(handler);
            var zone = _currentZoneProp.GetValue(handler);
            var turnManager = _turnManagerProp.GetValue(handler);
            var zoneRenderer = _zoneRendererProp.GetValue(handler);

            if (player == null || zone == null || turnManager == null)
                return new ErrorResponse("Game not ready (player/zone/turnManager is null).");

            var (playerX, playerY) = GetEntityPosition(zone, player);
            int maxSteps = p.GetInt("max_steps") ?? 50;

            // Determine target coordinates
            int targetX, targetY;
            string targetName = p.Get("target");

            if (!string.IsNullOrEmpty(targetName))
            {
                // Find entity by name — target an adjacent passable cell
                var found = FindEntityByName(zone, targetName);
                if (found == null)
                    return new ErrorResponse($"No entity named '{targetName}' found in this zone.");

                var (ex, ey) = GetEntityPosition(zone, found);
                var adj = FindAdjacentPassable(zone, ex, ey, playerX, playerY);
                if (adj == null)
                    return new ErrorResponse($"No passable cell adjacent to '{targetName}' at ({ex},{ey}).");

                targetX = adj.Value.x;
                targetY = adj.Value.y;
            }
            else
            {
                var rawX = p.GetInt("x");
                var rawY = p.GetInt("y");
                if (rawX == null || rawY == null)
                    return new ErrorResponse("Provide 'target' (entity name) or 'x' and 'y' coordinates.");
                targetX = rawX.Value;
                targetY = rawY.Value;
            }

            if (playerX == targetX && playerY == targetY)
                return new SuccessResponse("Already at target.", new
                {
                    position = new[] { playerX, playerY },
                    steps_taken = 0
                });

            // BFS pathfinding (MCP-only — not used during normal gameplay)
            var path = BFSPath(zone, playerX, playerY, targetX, targetY);
            if (path == null)
                return new ErrorResponse($"No walkable path from ({playerX},{playerY}) to ({targetX},{targetY}).");

            if (path.Count > maxSteps)
                path = path.GetRange(0, maxSteps);

            // Execute path step by step
            int stepsTaken = 0;
            string blockedBy = null;

            for (int i = 0; i < path.Count; i++)
            {
                var (dx, dy) = path[i];
                var (curX, curY) = GetEntityPosition(zone, player);

                // Call MovementSystem.TryMoveEx(player, zone, dx, dy)
                object moveResult;
                try
                {
                    moveResult = _tryMoveExMethod.Invoke(null, new[] { player, zone, (object)dx, (object)dy });
                }
                catch (Exception e)
                {
                    return new ErrorResponse($"Movement failed at step {i}: {e.InnerException?.Message ?? e.Message}");
                }

                // Parse (bool moved, Entity blockedBy) tuple
                var resultType = moveResult.GetType();
                bool moved = (bool)resultType.GetField("Item1").GetValue(moveResult);
                var blocker = resultType.GetField("Item2").GetValue(moveResult);

                if (!moved)
                {
                    blockedBy = blocker != null ? GetDisplayName(blocker) : "obstacle";
                    break;
                }

                stepsTaken++;

                // End turn and process NPCs (same as InputHandler.EndTurnAndProcess)
                _endTurnMethod.Invoke(turnManager, new[] { player, zone });
                _processUntilPlayerTurnMethod.Invoke(turnManager, null);

                // Refresh renderer
                if (_markDirtyMethod != null && zoneRenderer != null)
                    _markDirtyMethod.Invoke(zoneRenderer, new object[] { "MCP.MoveTo" });
            }

            var (finalX, finalY) = GetEntityPosition(zone, player);

            var result = new Dictionary<string, object>
            {
                ["steps_taken"] = stepsTaken,
                ["path_length"] = path.Count,
                ["position"] = new[] { finalX, finalY },
            };

            if (blockedBy != null)
            {
                result["blocked_by"] = blockedBy;
                return new SuccessResponse(
                    $"Moved {stepsTaken}/{path.Count} steps to ({finalX},{finalY}). Blocked by {blockedBy}.", result);
            }

            return new SuccessResponse($"Moved {stepsTaken} steps to ({finalX},{finalY}).", result);
        }

        // =====================================================================
        // BFS pathfinder — MCP-only, not used during normal gameplay
        // =====================================================================

        private static List<(int dx, int dy)> BFSPath(object zone, int startX, int startY, int goalX, int goalY)
        {
            int w = GetZoneWidth();
            int h = GetZoneHeight();

            var visited = new bool[w, h];
            var parent = new (int px, int py)[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    parent[x, y] = (-1, -1);

            var queue = new Queue<(int x, int y)>();
            queue.Enqueue((startX, startY));
            visited[startX, startY] = true;

            // 8-directional deltas
            int[] dxs = { 0, 1, 1, 1, 0, -1, -1, -1 };
            int[] dys = { -1, -1, 0, 1, 1, 1, 0, -1 };

            bool found = false;

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                if (cx == goalX && cy == goalY)
                {
                    found = true;
                    break;
                }

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + dxs[d];
                    int ny = cy + dys[d];

                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    if (visited[nx, ny]) continue;

                    // Allow goal cell even if solid (we might want to move adjacent)
                    // Allow start cell always
                    if (nx != goalX || ny != goalY)
                    {
                        var cell = GetCell(zone, nx, ny);
                        if (IsCellSolid(cell)) continue;
                    }

                    visited[nx, ny] = true;
                    parent[nx, ny] = (cx, cy);
                    queue.Enqueue((nx, ny));
                }
            }

            if (!found) return null;

            // Reconstruct path as deltas
            var path = new List<(int dx, int dy)>();
            int bx = goalX, by = goalY;
            while (bx != startX || by != startY)
            {
                var (px, py) = parent[bx, by];
                path.Add((bx - px, by - py));
                bx = px;
                by = py;
            }
            path.Reverse();
            return path;
        }

        private static object FindEntityByName(object zone, string name)
        {
            int w = GetZoneWidth();
            int h = GetZoneHeight();
            string lower = name.ToLowerInvariant();

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    var cell = GetCell(zone, x, y);
                    if (cell == null) continue;
                    var objects = GetCellObjects(cell);
                    if (objects == null) continue;
                    foreach (var entity in objects)
                    {
                        var dispName = GetDisplayName(entity);
                        var bpName = _entityType.GetField("BlueprintName")?.GetValue(entity)?.ToString();
                        if ((dispName != null && dispName.ToLowerInvariant().Contains(lower)) ||
                            (bpName != null && bpName.ToLowerInvariant().Contains(lower)))
                            return entity;
                    }
                }
            }
            return null;
        }

        private static (int x, int y)? FindAdjacentPassable(object zone, int targetX, int targetY, int fromX, int fromY)
        {
            // Find the closest passable cell adjacent to target, preferring cells closer to fromX/fromY
            int[] dxs = { 0, 1, 1, 1, 0, -1, -1, -1 };
            int[] dys = { -1, -1, 0, 1, 1, 1, 0, -1 };

            (int x, int y)? best = null;
            int bestDist = int.MaxValue;

            for (int d = 0; d < 8; d++)
            {
                int nx = targetX + dxs[d];
                int ny = targetY + dys[d];
                var cell = GetCell(zone, nx, ny);
                if (cell == null || IsCellSolid(cell)) continue;
                int dist = Math.Max(Math.Abs(nx - fromX), Math.Abs(ny - fromY));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = (nx, ny);
                }
            }
            return best;
        }

        // =====================================================================
        // query_surroundings — MCP-only world observation for AI assistants
        // =====================================================================

        public static object QuerySurroundings(ToolParams p)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("Game reflection not available.");

            var handler = GetHandler();
            if (handler == null)
                return new ErrorResponse("InputHandler not found.");

            var player = _playerEntityProp.GetValue(handler);
            var zone = _currentZoneProp.GetValue(handler);
            if (player == null || zone == null)
                return new ErrorResponse("Game not ready.");

            int radius = p.GetInt("radius") ?? 8;
            var (px, py) = GetEntityPosition(zone, player);

            // Player info
            var playerStats = new Dictionary<string, object>();
            try
            {
                var stats = _entityType.GetField("Statistics")?.GetValue(player) as System.Collections.IDictionary;
                if (stats != null)
                {
                    foreach (System.Collections.DictionaryEntry entry in stats)
                    {
                        var stat = entry.Value;
                        var valProp = stat.GetType().GetProperty("Value");
                        var maxProp = stat.GetType().GetProperty("Max");
                        if (valProp != null)
                        {
                            var val = valProp.GetValue(stat);
                            var max = maxProp?.GetValue(stat);
                            playerStats[entry.Key.ToString()] = max != null ? $"{val}/{max}" : val.ToString();
                        }
                    }
                }
            }
            catch { /* stats reading failed, continue without */ }

            // Scan nearby entities
            var creatures = new List<Dictionary<string, object>>();
            var items = new List<Dictionary<string, object>>();
            var structures = new List<Dictionary<string, object>>();

            int w = GetZoneWidth();
            int h = GetZoneHeight();

            for (int x = Math.Max(0, px - radius); x <= Math.Min(w - 1, px + radius); x++)
            {
                for (int y = Math.Max(0, py - radius); y <= Math.Min(h - 1, py + radius); y++)
                {
                    var cell = GetCell(zone, x, y);
                    if (cell == null) continue;
                    var objects = GetCellObjects(cell);
                    if (objects == null) continue;

                    foreach (var entity in objects)
                    {
                        if (entity.Equals(player)) continue;

                        string name = null;
                        try { name = GetDisplayName(entity); } catch { continue; }
                        if (name == null || name == "floor" || name == "wall") continue;

                        int dist = Math.Max(Math.Abs(x - px), Math.Abs(y - py));
                        var info = new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["position"] = new[] { x, y },
                            ["distance"] = dist,
                        };

                        if (HasTag(entity, "Creature"))
                        {
                            string faction = GetTag(entity, "Faction") ?? "none";
                            info["faction"] = faction;

                            // Check hostility
                            if (_factionManagerInstanceProp != null && _isHostileMethod != null)
                            {
                                try
                                {
                                    var fm = _factionManagerInstanceProp.GetValue(null);
                                    if (fm != null)
                                        info["hostile"] = _isHostileMethod.Invoke(fm, new[] { entity, player });
                                }
                                catch { }
                            }

                            // HP
                            try
                            {
                                var entityStats = _entityType.GetField("Statistics")?.GetValue(entity) as System.Collections.IDictionary;
                                if (entityStats != null && entityStats.Contains("Hitpoints"))
                                {
                                    var hp = entityStats["Hitpoints"];
                                    var val = hp.GetType().GetProperty("Value")?.GetValue(hp);
                                    var max = hp.GetType().GetProperty("Max")?.GetValue(hp);
                                    info["hp"] = $"{val}/{max}";
                                }
                            }
                            catch { }

                            creatures.Add(info);
                        }
                        else if (HasTag(entity, "Item"))
                        {
                            var phys = GetPart(entity, _physicsPartType);
                            if (phys != null)
                                info["takeable"] = _takeableProp != null ? _takeableProp.GetValue(phys) : _takeableField?.GetValue(phys);

                            var grim = GetPart(entity, _grimoirePartType);
                            if (grim != null)
                            {
                                info["grimoire"] = true;
                                var mc = _mutationClassNameField?.GetValue(grim)?.ToString();
                                var kp = _knowledgePropertyField?.GetValue(grim)?.ToString();
                                if (!string.IsNullOrEmpty(mc)) info["mutation"] = mc;
                                if (!string.IsNullOrEmpty(kp)) info["knowledge"] = kp;
                            }

                            items.Add(info);
                        }
                        else if (HasTag(entity, "Solid") || HasTag(entity, "Terrain"))
                        {
                            // Skip basic terrain, only report interesting structures
                            var bp = _entityType.GetField("BlueprintName")?.GetValue(entity)?.ToString();
                            if (bp != null && bp != "Floor" && bp != "Wall" && bp != "CaveWall")
                                structures.Add(info);
                        }
                    }
                }
            }

            // Build ASCII mini-map
            int mapRadius = Math.Min(radius, 10);
            var mapLines = new List<string>();
            for (int y = py - mapRadius; y <= py + mapRadius; y++)
            {
                var line = new System.Text.StringBuilder();
                for (int x = px - mapRadius; x <= px + mapRadius; x++)
                {
                    if (x == px && y == py) { line.Append('@'); continue; }
                    if (x < 0 || x >= w || y < 0 || y >= h) { line.Append(' '); continue; }
                    var cell = GetCell(zone, x, y);
                    if (cell == null) { line.Append(' '); continue; }
                    if (IsCellSolid(cell)) { line.Append('#'); continue; }
                    var objs = GetCellObjects(cell);
                    if (objs != null && objs.Count > 0)
                    {
                        var top = objs[objs.Count - 1];
                        var rend = GetPart(top, _renderPartType);
                        if (rend != null)
                        {
                            var renderStr = _renderStringField?.GetValue(rend)?.ToString();
                            if (renderStr != null && renderStr.Length > 0)
                            {
                                line.Append(renderStr[0]);
                                continue;
                            }
                        }
                    }
                    line.Append('.');
                }
                mapLines.Add(line.ToString());
            }

            return new SuccessResponse("Surroundings queried.", new Dictionary<string, object>
            {
                ["player"] = new { position = new[] { px, py }, stats = playerStats },
                ["creatures"] = creatures,
                ["items"] = items,
                ["structures"] = structures,
                ["map"] = mapLines,
            });
        }

        // =====================================================================
        // wait_turns — MCP-only turn passing for AI assistants
        // =====================================================================

        public static object WaitTurns(ToolParams p)
        {
            if (!EnsureResolved() || !_available)
                return new ErrorResponse("Game reflection not available.");

            var handler = GetHandler();
            if (handler == null)
                return new ErrorResponse("InputHandler not found.");

            var player = _playerEntityProp.GetValue(handler);
            var zone = _currentZoneProp.GetValue(handler);
            var turnManager = _turnManagerProp.GetValue(handler);
            var zoneRenderer = _zoneRendererProp.GetValue(handler);

            if (player == null || zone == null || turnManager == null)
                return new ErrorResponse("Game not ready.");

            int count = p.GetInt("count") ?? 1;
            count = Math.Min(count, 100); // Safety cap

            for (int i = 0; i < count; i++)
            {
                _endTurnMethod.Invoke(turnManager, new[] { player, zone });
                _processUntilPlayerTurnMethod.Invoke(turnManager, null);
            }

            if (_markDirtyMethod != null && zoneRenderer != null)
                _markDirtyMethod.Invoke(zoneRenderer, new object[] { "MCP.WaitTurns" });

            // Return current state
            var (px, py) = GetEntityPosition(zone, player);
            var entityStats = _entityType.GetField("Statistics")?.GetValue(player) as System.Collections.IDictionary;
            string hp = "?";
            if (entityStats != null && entityStats.Contains("Hitpoints"))
            {
                var hpStat = entityStats["Hitpoints"];
                var val = hpStat.GetType().GetProperty("Value")?.GetValue(hpStat);
                var max = hpStat.GetType().GetProperty("Max")?.GetValue(hpStat);
                hp = $"{val}/{max}";
            }

            return new SuccessResponse($"Waited {count} turn(s).", new
            {
                turns_waited = count,
                position = new[] { px, py },
                hp,
            });
        }
    }
}
