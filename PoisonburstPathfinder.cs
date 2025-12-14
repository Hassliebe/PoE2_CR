using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace PoisonburstPathfinder;
public class PoisonburstPathfinder : BaseSettingsPlugin<PoisonburstSettings>
{
    private readonly Stopwatch _flaskTimer = Stopwatch.StartNew();
    private readonly Stopwatch _skillRuleTimer = Stopwatch.StartNew();

    private readonly List<(string SkillId, string Name)> _cachedSkillBar = new();
    private readonly SimpleRaycaster _raycaster = new();
    private readonly Dictionary<SkillRule, Stopwatch> _ruleCooldowns = new();
    private Entity _currentAimTarget;
    private int _growthCount;
    private readonly HashSet<uint> _growthCountIds = new();
    private bool _cursorInGas;
    private Entity _vineArrow;
    private Entity _gasArrow;
    private string _logFilePath;

    private int _lastMonsterCount;
    private float _lastNearestDistance;
    private bool _debugWindowVisible;
    private bool _skillWindowVisible;
    private bool _aimAssistToggled;
    private Vector2 _windowTopLeft;
    private Func<bool> _pickitIsActive;
    private RectangleF _windowRect;

    public PoisonburstPathfinder()
    {
        Name = "Poisonburst Pathfinder";
    }

    public override bool Initialise()
    {
        var baseDir = Path.Combine(Environment.CurrentDirectory, "Plugins", "Source", "PoisonburstPathfinder");
        _logFilePath = Path.Combine(baseDir, "PoisonburstPathfinder.log");

        RegisterConfiguredKeys();

        Settings.Flasks.LifeFlaskKey.OnValueChanged += RegisterConfiguredKeys;
        Settings.Flasks.ManaFlaskKey.OnValueChanged += RegisterConfiguredKeys;
        Settings.Debug.ToggleWindowKey.OnValueChanged += RegisterConfiguredKeys;
        Settings.SkillRules.ToggleEditorWindow.OnPressed += () =>
        {
            Settings.SkillRules.ShowEditorWindow.Value = true;
            _skillWindowVisible = true;
        };
        Settings.AimAssist.AimKey.OnValueChanged += RegisterConfiguredKeys;
        Settings.AimAssist.AimToggleKey.OnValueChanged += RegisterConfiguredKeys;

        if (Settings.Debug.EnableFileLog || Settings.Debug.LogRotation)
            AppendToLog("Poisonburst initialised");
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        base.AreaChange(area);

        _flaskTimer.Restart();
        _skillRuleTimer.Restart();

        _lastMonsterCount = 0;
        _lastNearestDistance = 0;

        _pickitIsActive = GameController.PluginBridge.GetMethod<Func<bool>>("PickIt.IsActive");
        _raycaster.UpdateArea(GameController);
    }

    public override void Tick()
    {
        if (!Settings.Enable) return;

        if (Settings.Debug.ToggleWindowKey.PressedOnce())
            _debugWindowVisible = !_debugWindowVisible;
        if (Settings.AimAssistMode && Settings.AimAssist.AimToggleKey.PressedOnce())
            _aimAssistToggled = !_aimAssistToggled;
        if (GameController == null || !GameController.InGame || GameController.IsLoading) return;
        if (Settings.General.RequireGameFocus && GameController.Window?.IsForeground() == false) return;
        if (Settings.General.PauseOnEscapeState && GameController.Game.IsEscapeState) return;

        var ingameState = GameController.IngameState;
        if (ingameState?.IngameUi == null) return;
        if (ShouldPauseForUi(ingameState.IngameUi)) return;

        var area = GameController.Area?.CurrentArea;
        if (!Settings.General.RunInTown && area is { IsTown: true }) return;

        var windowRect = GameController.Window.GetWindowRectangleReal();
        _windowRect = new RectangleF(windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height);
        _windowTopLeft = GameController.Window.GetWindowRectangleTimeCache.TopLeft;

        var player = GameController.Player;
        if (player == null) return;

        _raycaster.UpdateObserver(player.GridPos, GameController);

        var scanRange = Settings.AimAssistMode ? Settings.AimAssist.EngageRange.Value : 0; // 0 = no cap in rotation-only
        var monsters = GetHostileMonsters(scanRange);
        _lastMonsterCount = monsters.Count;
        _lastNearestDistance = monsters.Count > 0 ? monsters.Min(m => m.DistancePlayer) : 0f;
        _currentAimTarget = monsters.FirstOrDefault();

        var lifePercent = TryGetEffectiveLifePercent(player);
        var manaPercent = TryGetManaPercent(player);

        TryUseFlasks(lifePercent, manaPercent);
        HandleAimAssist(monsters);
        TryUseSkillRules(monsters, lifePercent);

        UpdateSkillBarCache();
        UpdateGroundEffectInfo();
    }

    public override void Render()
    {
        var cameraReady = GameController?.IngameState?.Camera != null;

        if (Settings.Debug.ShowOverlay && cameraReady)
        {
            var aimMode = Settings.AimAssistMode
                ? _aimAssistToggled || Settings.AimAssist.AimKey.IsPressed()
                    ? "AimAssist: active"
                    : "AimAssist: idle"
                : "AimAssist: off";

            var text = Settings.Debug.ShowCounts
                ? $"Poisonburst: mobs {_lastMonsterCount}" +
                  (_lastNearestDistance > 0 ? $" | nearest {_lastNearestDistance:0.0}" : string.Empty) +
                  $" | {aimMode}"
                : $"Poisonburst Pathfinder | {aimMode}";

            var position = new Vector2(Settings.Debug.OverlayX, Settings.Debug.OverlayY);
            Graphics.DrawText(text, position, Settings.Debug.OverlayColor);

            if (Settings.Debug.ShowTargetDetails)
            {
                var yOffset = position.Y + 18;
                var rarityText = _currentAimTarget?.TryGetComponent<ObjectMagicProperties>(out var props) == true
                    ? props.Rarity.ToString()
                    : "None";
                Graphics.DrawText($"Target rarity: {rarityText}", new Vector2(position.X, yOffset), Settings.Debug.OverlayColor);
                yOffset += 16;
                Graphics.DrawText($"Vine Arrow: {(_vineArrow?.Path ?? "none")}", new Vector2(position.X, yOffset), Settings.Debug.OverlayColor);
                yOffset += 16;
                Graphics.DrawText($"Gas Arrow: {(_gasArrow?.Path ?? "none")}", new Vector2(position.X, yOffset), Settings.Debug.OverlayColor);
                yOffset += 16;
                Graphics.DrawText($"Cursor in gas: {_cursorInGas}", new Vector2(position.X, yOffset), Settings.Debug.OverlayColor);
                yOffset += 16;
                Graphics.DrawText($"Toxic growths: {_growthCount}", new Vector2(position.X, yOffset), Settings.Debug.OverlayColor);
            }
        }

        if (Settings.Debug.EnableWindow && _debugWindowVisible)
        {
            DrawDebugWindow();
        }

        if (_skillWindowVisible)
        {
            DrawSkillRulesWindow();
        }
    }

    private void TryUseFlasks(float lifePct, float manaPct)
    {
        if (Settings.Flasks.EnableLifeFlask &&
            Settings.Flasks.LifeFlaskKey.Value.Key != Keys.None &&
            lifePct > 0 && lifePct <= Settings.Flasks.LifeThresholdPct.Value &&
            Ready(_flaskTimer, Settings.Flasks.LifeFlaskCooldownMs.Value))
        {
            Press(Settings.Flasks.LifeFlaskKey.Value);
            _flaskTimer.Restart();
            LogDebug($"Life flask at {lifePct:0.#}%");
        }

        if (Settings.Flasks.EnableManaFlask &&
            Settings.Flasks.ManaFlaskKey.Value.Key != Keys.None &&
            manaPct > 0 && manaPct <= Settings.Flasks.ManaThresholdPct.Value &&
            Ready(_flaskTimer, Settings.Flasks.ManaFlaskCooldownMs.Value))
        {
            Press(Settings.Flasks.ManaFlaskKey.Value);
            _flaskTimer.Restart();
            LogDebug($"Mana flask at {manaPct:0.#}%");
        }
    }

    private List<Entity> GetHostileMonsters(int range)
    {
        var results = new List<Entity>();
        if (GameController?.EntityListWrapper?.ValidEntitiesByType == null) return results;

        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (!IsValidMonster(entity)) continue;
            if (range > 0 && entity.DistancePlayer > range) continue;
            results.Add(entity);
        }

        return results;
    }

    private static bool IsValidMonster(Entity entity)
    {
        if (entity == null) return false;
        if (!entity.IsValid || entity.IsDead || !entity.IsAlive) return false;
        if (!entity.IsHostile || !entity.IsTargetable || entity.IsHidden) return false;
        return !(entity.Stats.TryGetValue(GameStat.CannotBeDamaged, out var invuln) && invuln == 1);
    }

    private static bool Ready(Stopwatch stopwatch, int thresholdMs)
    {
        return stopwatch.ElapsedMilliseconds >= thresholdMs;
    }

    private float TryGetEffectiveLifePercent(Entity player)
    {
        if (!player.TryGetComponent<Life>(out var life)) return -1f;

        var current = life.CurHP + life.CurES;
        var maximum = Math.Max(1, life.MaxHP + life.MaxES);
        return current * 100f / maximum;
    }

    private float TryGetManaPercent(Entity player)
    {
        if (!player.TryGetComponent<Life>(out var life)) return -1f;
        var current = life.CurMana;
        var maximum = Math.Max(1, life.MaxMana);
        return current * 100f / maximum;
    }

    private bool ShouldPauseForUi(IngameUIElements ui)
    {
        if (ui == null) return false;

        if (Settings.General.PauseWhenPanelsOpen &&
            (ui.FullscreenPanels.Any(p => p.IsVisible) ||
             ui.LargePanels.Any(p => p.IsVisible) ||
             ui.OpenLeftPanel.IsVisible ||
             ui.OpenRightPanel.IsVisible))
        {
            return true;
        }

        if (Settings.General.PauseWhenChatOpen && ui.ChatTitlePanel.IsVisible)
        {
            return true;
        }

        return false;
    }

    private void HandleAimAssist(IReadOnlyCollection<Entity> monsters)
    {
        if (!Settings.AimAssistMode) return;
        if (monsters.Count == 0) return;
        if (_pickitIsActive?.Invoke() ?? false) return; // respect PickIt lock

        var holdActive = Settings.AimAssist.AimKey.IsPressed();
        var toggleActive = _aimAssistToggled;
        if (!holdActive && !toggleActive) return;

        var target = monsters
            .Where(m => _raycaster.IsVisible(m.GridPos))
            .OrderBy(m => m.DistancePlayer)
            .FirstOrDefault();
        if (target == null) return;

        _currentAimTarget = target;

        var screenPos = GameController.IngameState.Camera.WorldToScreen(target.Pos);
        if (screenPos == Vector2.Zero) return;

        var posWithOffset = screenPos + _windowTopLeft;
        if (WithinWindow(posWithOffset))
        {
            Input.SetCursorPos(posWithOffset);
        }
    }

    private bool WithinWindow(Vector2 pos)
    {
        return pos.X >= _windowRect.Left &&
               pos.X <= _windowRect.Right &&
               pos.Y >= _windowRect.Top &&
               pos.Y <= _windowRect.Bottom;
    }

    private void Press(HotkeyNodeV2.HotkeyNodeValue key)
    {
        if (!Settings.General.RequireGameFocus || GameController.Window.IsForeground())
        {
            // Handle mouse buttons explicitly; other keys go through InputHelper.
            switch (key.Key)
            {
                case Keys.LButton:
                    Input.Click(MouseButtons.Left);
                    break;
                case Keys.RButton:
                    Input.Click(MouseButtons.Right);
                    break;
                case Keys.MButton:
                    Input.Click(MouseButtons.Middle);
                    break;
                default:
                    InputHelper.SendInputPress(key);
                    break;
            }
        }
    }

    private void RegisterConfiguredKeys()
    {
        Input.RegisterKey(Settings.Flasks.LifeFlaskKey.Value);
        Input.RegisterKey(Settings.Flasks.ManaFlaskKey.Value);
        Input.RegisterKey(Settings.Debug.ToggleWindowKey.Value);
        Input.RegisterKey(Settings.AimAssist.AimKey.Value);
        Input.RegisterKey(Settings.AimAssist.AimToggleKey.Value);
    }

    private void TryUseSkillRules(IReadOnlyCollection<Entity> monsters, float lifePct)
    {
        if (!Settings.SkillRules.Enable) return;
        if (!Ready(_skillRuleTimer, Settings.SkillRules.GlobalCooldownMs.Value)) return;

        foreach (var rule in Settings.SkillRules.Rules.ToList())
        {
            if (rule == null || rule.Key == Keys.None) continue;
            if (!SkillConditionMet(rule, rule.Condition, monsters, lifePct)) continue;

            var hotkey = new HotkeyNodeV2.HotkeyNodeValue(rule.Key);
            Press(hotkey);
            _skillRuleTimer.Restart();
            LogDebug($"Rule fired: {rule.SkillName} ({rule.Key})");
            LogRotation($"Rule fired: {rule.SkillName} ({rule.Key}), monsters={monsters.Count}, life={lifePct:0.#}%");
            MarkRuleUsed(rule);
            break;
        }
    }

    private bool SkillConditionMet(SkillRule rule, SkillCondition condition, IReadOnlyCollection<Entity> monsters, float lifePct)
    {
        if (condition == null) return false;
        if (condition.MinCooldownMs > 0 && !RuleReady(rule, condition.MinCooldownMs)) return false;

        var filtered = monsters.Where(m => PassesRarity(condition.RarityFilter, m)).ToList();
        if (condition.RequireCursorNearby)
        {
            filtered = filtered.Where(IsNearCursor(condition.CursorRange)).ToList();
            if (condition.MinMonstersInRange > 0 && filtered.Count < condition.MinMonstersInRange) return false;
        }
        else if (condition.MinMonstersInRange > 0)
        {
            filtered = filtered.Where(m => m.DistancePlayer <= condition.Range).ToList();
            if (filtered.Count < condition.MinMonstersInRange) return false;
        }

        if (condition.OnlyIfBossPresent && !filtered.Any(IsBoss)) return false;
        if (condition.MinLifePercent > 0 && lifePct > 0 && lifePct < condition.MinLifePercent) return false;

        if (Settings.General.RequireGameFocus && GameController.Window?.IsForeground() == false) return false;

        if (condition.SkipIfDeployedObjectNearby)
        {
            if (HasOwnedDeployedNearby(rule, condition)) return false;
            if (HasFallbackEntityNearby(condition)) return false;
        }

        return true;
    }

    private Func<Entity, bool> IsNearCursor(int cursorRange)
    {
        var cursor = Input.ForceMousePosition;
        var camera = GameController?.IngameState?.Camera;
        if (camera == null) return _ => false;

        return m =>
        {
            var screenPos = camera.WorldToScreen(m.Pos) + _windowTopLeft;
            return Vector2.Distance(cursor, screenPos) <= cursorRange;
        };
    }

    private bool HasOwnedDeployedNearby(SkillRule rule, SkillCondition condition)
    {
        var player = GameController.Player;
        if (player == null) return false;

        var actor = player.GetComponent<Actor>();
        if (actor?.ActorSkills == null) return false;

        var cursorRequired = condition.RequireCursorNearby;
        var cursorPos = cursorRequired ? Input.ForceMousePosition : Vector2.Zero;
        var camera = cursorRequired ? GameController?.IngameState?.Camera : null;

        var targetRange = cursorRequired ? condition.CursorRange : condition.DeployedObjectRange;

        foreach (var skill in actor.ActorSkills)
        {
            if (skill == null) continue;
            // If we can match the skill id, prefer that; otherwise any deployed object counts.
            if (!string.IsNullOrEmpty(rule.SkillId) && !skill.Id.ToString().Equals(rule.SkillId, StringComparison.OrdinalIgnoreCase))
                continue;

            var deployed = skill.DeployedObjects;
            if (deployed == null) continue;

            foreach (var obj in deployed)
            {
                if (obj?.Entity == null) continue;
                if (cursorRequired && camera != null)
                {
                    var screenPos = camera.WorldToScreen(obj.Entity.Pos) + _windowTopLeft;
                    if (Vector2.Distance(cursorPos, screenPos) <= targetRange)
                        return true;
                }
                else
                {
                    if (obj.Entity.DistancePlayer <= targetRange)
                        return true;
                }
            }
        }

        return false;
    }

    private bool HasFallbackEntityNearby(SkillCondition condition)
    {
        if (condition.FallbackEntityPathContains == null || condition.FallbackEntityPathContains.Count == 0)
            return false;

        var cursorRequired = condition.RequireCursorNearby;
        var cursorPos = cursorRequired ? Input.ForceMousePosition : Vector2.Zero;
        var camera = cursorRequired ? GameController?.IngameState?.Camera : null;

        var targetRange = cursorRequired ? condition.CursorRange : condition.DeployedObjectRange;

        var allEntities = GameController?.EntityListWrapper?.ValidEntitiesByType?
            .SelectMany(kv => kv.Value) ?? Enumerable.Empty<Entity>();

        foreach (var entity in allEntities)
        {
            if (entity?.Path == null) continue;
            if (!condition.FallbackEntityPathContains.Any(s => entity.Path.Contains(s, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (cursorRequired && camera != null)
            {
                var screenPos = camera.WorldToScreen(entity.Pos) + _windowTopLeft;
                if (Vector2.Distance(cursorPos, screenPos) <= targetRange)
                    return true;
            }
            else
            {
                if (entity.DistancePlayer <= targetRange)
                    return true;
            }
        }

        return false;
    }

    private void UpdateGroundEffectInfo()
    {
        _vineArrow = null;
        _gasArrow = null;
        _growthCount = 0;
        _growthCountIds.Clear();
        _cursorInGas = false;

        var effectEntities = GameController?.EntityListWrapper?.ValidEntitiesByType?
            .TryGetValue(EntityType.Effect, out var list) == true
            ? list
            : Enumerable.Empty<Entity>();

        foreach (var entity in effectEntities)
        {
            var path = entity?.Path;
            if (string.IsNullOrEmpty(path)) continue;

            if (_vineArrow == null && PathMatches(path, "Metadata/Effects/Spells/bow_poison_bloom/PoisonBloom.ao"))
            {
                _vineArrow = entity;
            }
            else if (_gasArrow == null && PathMatches(path, "Metadata/Effects/Spells/crossbow_toxic_grenade/toxic_cloud.ao"))
            {
                _gasArrow = entity;
            }

            if (PathMatches(path, "Metadata/Effects/Spells/bow_toxic_pustule/pustule_01.ao"))
            {
                _growthCount++;
            }
        }

        // Fallback: scan animated base paths (as EffectZones does) across multiple lists
        if (_vineArrow == null || _gasArrow == null || _growthCount == 0)
        {
            var entityLists = new[]
            {
                GameController?.EntityListWrapper?.ValidEntitiesByType.TryGetValue(EntityType.Effect, out var e1) == true ? e1 : Enumerable.Empty<Entity>(),
                GameController?.EntityListWrapper?.ValidEntitiesByType.TryGetValue(EntityType.MonsterMods, out var e2) == true ? e2 : Enumerable.Empty<Entity>(),
                GameController?.EntityListWrapper?.ValidEntitiesByType.TryGetValue(EntityType.Terrain, out var e3) == true ? e3 : Enumerable.Empty<Entity>(),
                GameController?.EntityListWrapper?.ValidEntitiesByType.TryGetValue(EntityType.None, out var e4) == true ? e4 : Enumerable.Empty<Entity>(),
                GameController?.EntityListWrapper?.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var e5) == true ? e5 : Enumerable.Empty<Entity>(),
                GameController?.EntityListWrapper?.ValidEntitiesByType.TryGetValue(EntityType.MiscellaneousObjects, out var e6) == true ? e6 : Enumerable.Empty<Entity>(),
                GameController?.EntityListWrapper?.ValidEntitiesByType.TryGetValue(EntityType.IngameIcon, out var e7) == true ? e7 : Enumerable.Empty<Entity>()
            };

            foreach (var entity in entityLists.SelectMany(x => x))
            {
                if (entity == null) continue;

                string? basePath = null;
                if (entity.TryGetComponent<Animated>(out var animated) && animated.BaseAnimatedObjectEntity != null)
                    basePath = animated.BaseAnimatedObjectEntity.Path;
                basePath ??= entity.Path;

                if (string.IsNullOrEmpty(basePath)) continue;

                if (_vineArrow == null && (PathMatches(basePath, "Metadata/Effects/Spells/bow_poison_bloom/PoisonBloom.ao") ||
                                           PathMatches(basePath, "poison_bloom") ||
                                           PathMatches(basePath, "Metadata/MiscellaneousObjects/PoisonbloomArrow") ||
                                           PathMatches(basePath, "VineArrowBloom")))
                    _vineArrow = entity;
                if (_gasArrow == null && (PathMatches(basePath, "Metadata/Effects/Spells/crossbow_toxic_grenade/toxic_cloud.ao") ||
                                          PathMatches(basePath, "Metadata/MiscellaneousObjects/PoisonbloomArrow/ToxicCloud") ||
                                          PathMatches(basePath, "toxic_grenade") ||
                                          PathMatches(basePath, "toxic_cloud")))
                    _gasArrow = entity;
                if (PathMatches(basePath, "Metadata/MiscellaneousObjects/PoisonbloomArrow/Poisonbloom") ||
                    PathMatches(basePath, "Metadata/Effects/Spells/bow_toxic_pustule/pustule_01.ao") ||
                    PathMatches(basePath, "toxic_pustule") ||
                    PathMatches(basePath, "pustule_01"))
                    _growthCountIds.Add(entity.Id);

                if (_vineArrow != null && _gasArrow != null && _growthCountIds.Count > 0)
                    break;
            }
        }

        _growthCount = _growthCountIds.Count;

        // Cursor-in-gas check: simple screen-distance check to the gas arrow
        if (_gasArrow != null && GameController?.IngameState?.Camera is { } cam)
        {
            var screenPos = cam.WorldToScreen(_gasArrow.Pos) + _windowTopLeft;
            var cursor = Input.ForceMousePosition;
            _cursorInGas = Vector2.Distance(cursor, screenPos) <= 120; // configurable later if needed
        }
    }

    private bool RuleReady(SkillRule rule, int minCooldownMs)
    {
        if (!_ruleCooldowns.TryGetValue(rule, out var sw))
        {
            sw = Stopwatch.StartNew();
            _ruleCooldowns[rule] = sw;
            return true;
        }

        return sw.ElapsedMilliseconds >= minCooldownMs;
    }

    private void MarkRuleUsed(SkillRule rule)
    {
        if (!_ruleCooldowns.TryGetValue(rule, out var sw))
        {
            sw = Stopwatch.StartNew();
            _ruleCooldowns[rule] = sw;
        }
        else
        {
            sw.Restart();
        }
    }

    private sealed class SimpleRaycaster
    {
        private int[][] _terrainData = [];
        private Vector2 _areaDimensions;
        private Vector2 _observerPos;
        private readonly int _targetLayerValue = 2;

        public void UpdateArea(GameController controller)
        {
            var dims = controller?.IngameState?.Data?.AreaDimensions;
            var raw = controller?.IngameState?.Data?.RawTerrainTargetingData;
            if (dims == null || raw == null) return;

            _areaDimensions = dims.Value;
            _terrainData = new int[raw.Length][];
            for (var y = 0; y < raw.Length; y++)
            {
                _terrainData[y] = new int[raw[y].Length];
                Array.Copy(raw[y], _terrainData[y], raw[y].Length);
            }
        }

        public void UpdateObserver(Vector2 pos, GameController controller)
        {
            _observerPos = pos;
            if (_terrainData.Length == 0)
            {
                UpdateArea(controller);
            }
        }

        public bool IsVisible(Vector2 target)
        {
            if (_terrainData.Length == 0) return true;
            return HasLineOfSight(_observerPos, target);
        }

        private bool HasLineOfSight(Vector2 start, Vector2 end)
        {
            var startX = (int)start.X;
            var startY = (int)start.Y;
            var endX = (int)end.X;
            var endY = (int)end.Y;

            var dx = Math.Abs(endX - startX);
            var dy = Math.Abs(endY - startY);
            var x = startX;
            var y = startY;
            var stepX = startX < endX ? 1 : -1;
            var stepY = startY < endY ? 1 : -1;

            if (dx == 0 && dy == 0) return true;

            // Handle straight lines first
            if (dx == 0)
            {
                for (var i = 0; i < dy; i++)
                {
                    y += stepY;
                    if (!Passable(x, y)) return false;
                }
                return true;
            }

            if (dy == 0)
            {
                for (var i = 0; i < dx; i++)
                {
                    x += stepX;
                    if (!Passable(x, y)) return false;
                }
                return true;
            }

            // DDA-ish walk
            var driveByX = dx >= dy;
            if (driveByX)
            {
                var deltaErr = Math.Abs((float)dy / dx);
                var error = 0f;
                for (var i = 0; i < dx; i++)
                {
                    x += stepX;
                    error += deltaErr;
                    if (error >= 0.5f)
                    {
                        y += stepY;
                        error -= 1f;
                    }

                    if (!Passable(x, y)) return false;
                }
            }
            else
            {
                var deltaErr = Math.Abs((float)dx / dy);
                var error = 0f;
                for (var i = 0; i < dy; i++)
                {
                    y += stepY;
                    error += deltaErr;
                    if (error >= 0.5f)
                    {
                        x += stepX;
                        error -= 1f;
                    }

                    if (!Passable(x, y)) return false;
                }
            }

            return true;
        }

        private bool Passable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _areaDimensions.X || y >= _areaDimensions.Y)
                return false;
            var value = _terrainData[y][x];
            return value > _targetLayerValue;
        }
    }

    private static bool PassesRarity(MonsterRarityFilter filter, Entity entity)
    {
        if (!entity.TryGetComponent<ObjectMagicProperties>(out var props)) return false;
        return filter switch
        {
            MonsterRarityFilter.Any => true,
            MonsterRarityFilter.MagicOrHigher => props.Rarity >= MonsterRarity.Magic,
            MonsterRarityFilter.RareOrHigher => props.Rarity >= MonsterRarity.Rare,
            MonsterRarityFilter.UniqueOnly => props.Rarity == MonsterRarity.Unique,
            _ => true
        };
    }

    private static bool PathMatches(string path, string fragment)
    {
        return path?.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsBoss(Entity entity)
    {
        return entity.TryGetComponent<ObjectMagicProperties>(out var p) && p.Rarity == MonsterRarity.Unique;
    }

    private void UpdateSkillBarCache()
    {
        _cachedSkillBar.Clear();
        var skillBar = GameController.IngameState?.IngameUi?.SkillBar?.Skills;
        if (skillBar is { Count: > 0 })
        {
            foreach (var skillElement in skillBar)
            {
                var skill = skillElement?.Skill;
                if (skill == null) continue;
                var name = skill.InternalName ?? skill.Name ?? "Skill";
                _cachedSkillBar.Add((skill.Id.ToString(), name));
            }
            return;
        }

        // Fallback: use actor skills if UI skill bar is not populated (common in PoE2)
        var actor = GameController.Player?.GetComponent<Actor>();
        var actorSkills = actor?.ActorSkills;
        if (actorSkills == null) return;

        foreach (var skill in actorSkills)
        {
            if (skill == null) continue;
            var name = skill.InternalName ?? skill.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            _cachedSkillBar.Add((skill.Id.ToString(), name));
        }

        // Deduplicate by SkillId to keep the UI tidy.
        var deduped = _cachedSkillBar
            .GroupBy(x => x.SkillId)
            .Select(g => g.First())
            .ToList();

        _cachedSkillBar.Clear();
        _cachedSkillBar.AddRange(deduped);
    }

    private void DrawDebugWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 360), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Poisonburst Debug", ref _debugWindowVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"Mode: {(Settings.AimAssistMode ? "AimAssist" : "RotationOnly")}");
            ImGui.Text($"Monsters: {_lastMonsterCount} | nearest {_lastNearestDistance:0.0}");
            ImGui.Text($"Flask ready: {Ready(_flaskTimer, Math.Min(Settings.Flasks.LifeFlaskCooldownMs.Value, Settings.Flasks.ManaFlaskCooldownMs.Value))}");
            ImGui.Separator();

            if (ImGui.CollapsingHeader("Logging", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Text($"File: {_logFilePath}");
                ImGui.Text($"Enabled: {Settings.Debug.EnableFileLog.Value}");
            }
        }

        ImGui.End();
    }

    private void DrawSkillRulesWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(520, 480), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Poisonburst Skill Rules", ref _skillWindowVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("Add Rule"))
        {
            var firstSkill = _cachedSkillBar.FirstOrDefault();
            Settings.SkillRules.Rules.Add(new SkillRule
            {
                SkillId = firstSkill.SkillId ?? string.Empty,
                SkillName = firstSkill.Name ?? "Skill",
                Key = Keys.Q,
                Condition = new SkillCondition()
            });
        }

        ImGui.SameLine();
        ImGui.Text($"Detected skills: {_cachedSkillBar.Count}");

        var index = 0;
        foreach (var rule in Settings.SkillRules.Rules.ToList())
        {
            ImGui.PushID(index);

            var skillNames = _cachedSkillBar.Select(s => s.Name).ToArray();
            var selectedIndex = Math.Max(0, Array.IndexOf(skillNames, rule.SkillName));
            if (skillNames.Length == 0)
            {
                ImGui.Text("No skills detected.");
            }
            else if (ImGui.Combo("Skill", ref selectedIndex, skillNames, skillNames.Length))
            {
                var selected = _cachedSkillBar[selectedIndex];
                rule.SkillName = selected.Name;
                rule.SkillId = selected.SkillId;
            }

            var keyOptions = new[] { Keys.Q, Keys.W, Keys.E, Keys.R, Keys.Space, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.LButton, Keys.RButton, Keys.MButton };
            var keyLabels = keyOptions.Select(k => k.ToString()).ToArray();
            var keyIndex = Array.IndexOf(keyOptions, rule.Key);
            if (keyIndex < 0) keyIndex = 0;
            if (ImGui.Combo("Key", ref keyIndex, keyLabels, keyLabels.Length))
            {
                rule.Key = keyOptions[keyIndex];
            }

            var cond = rule.Condition ?? new SkillCondition();
            int minCount = cond.MinMonstersInRange;
            int range = cond.Range;
            int minLife = cond.MinLifePercent;
            int cursorRange = cond.CursorRange;

            if (ImGui.SliderInt("Min monsters", ref minCount, 0, 20))
                cond.MinMonstersInRange = minCount;
            if (ImGui.SliderInt("Range", ref range, 10, 150))
                cond.Range = range;
            if (ImGui.SliderInt("Min life %", ref minLife, 0, 100))
                cond.MinLifePercent = minLife;

            var rarityOptions = Enum.GetNames(typeof(MonsterRarityFilter));
            var rarityIndex = (int)cond.RarityFilter;
            if (ImGui.Combo("Rarity", ref rarityIndex, rarityOptions, rarityOptions.Length))
                cond.RarityFilter = (MonsterRarityFilter)rarityIndex;

            var requireBoss = cond.OnlyIfBossPresent;
            if (ImGui.Checkbox("Only if boss present", ref requireBoss))
                cond.OnlyIfBossPresent = requireBoss;

            var cursorNear = cond.RequireCursorNearby;
            if (ImGui.Checkbox("Require cursor nearby", ref cursorNear))
                cond.RequireCursorNearby = cursorNear;
            if (cond.RequireCursorNearby && ImGui.SliderInt("Cursor range (px)", ref cursorRange, 20, 400))
                cond.CursorRange = cursorRange;

            int minCd = cond.MinCooldownMs;
            if (ImGui.SliderInt("Min cooldown (ms)", ref minCd, 0, 10000))
                cond.MinCooldownMs = minCd;

            var skipDeployed = cond.SkipIfDeployedObjectNearby;
            if (ImGui.Checkbox("Skip if deployed object nearby", ref skipDeployed))
                cond.SkipIfDeployedObjectNearby = skipDeployed;
            int deployedRange = cond.DeployedObjectRange;
            if (cond.SkipIfDeployedObjectNearby && ImGui.SliderInt("Deployed range", ref deployedRange, 20, 400))
                cond.DeployedObjectRange = deployedRange;

            if (cond.SkipIfDeployedObjectNearby)
            {
                ImGui.Text("Fallback entity path contains:");
                var toRemove = -1;
                for (var i = 0; i < cond.FallbackEntityPathContains.Count; i++)
                {
                    ImGui.PushID(i);
                    var str = cond.FallbackEntityPathContains[i];
                    var buf = str ?? string.Empty;
                    var changed = ImGui.InputText("##path", ref buf, 128);
                    ImGui.SameLine();
                    if (ImGui.Button("X"))
                        toRemove = i;
                    if (changed)
                        cond.FallbackEntityPathContains[i] = buf;
                    ImGui.PopID();
                }
                if (toRemove >= 0 && toRemove < cond.FallbackEntityPathContains.Count)
                    cond.FallbackEntityPathContains.RemoveAt(toRemove);
                if (ImGui.Button("Add path filter"))
                    cond.FallbackEntityPathContains.Add(string.Empty);
            }

            ImGui.Separator();
            if (ImGui.Button("Move Up") && index > 0)
            {
                SwapRules(index, index - 1);
                ImGui.PopID();
                break;
            }
            ImGui.SameLine();
            if (ImGui.Button("Move Down") && index < Settings.SkillRules.Rules.Count - 1)
            {
                SwapRules(index, index + 1);
                ImGui.PopID();
                break;
            }

            if (ImGui.Button("Delete"))
            {
                Settings.SkillRules.Rules.Remove(rule);
                ImGui.PopID();
                index++;
                continue;
            }

            ImGui.Separator();
            ImGui.PopID();
            index++;
        }

        ImGui.End();
    }

    private void SwapRules(int a, int b)
    {
        if (a < 0 || b < 0 || a >= Settings.SkillRules.Rules.Count || b >= Settings.SkillRules.Rules.Count)
            return;

        (Settings.SkillRules.Rules[a], Settings.SkillRules.Rules[b]) = (Settings.SkillRules.Rules[b], Settings.SkillRules.Rules[a]);
    }

    private void LogRotation(string message)
    {
        if (!Settings.Debug.LogRotation) return;
        AppendToLog(message);
    }

    private void LogDebug(string message)
    {
        if (Settings.Debug.LogLevel.Value <= 0) return;
        if (!Settings.Debug.EnableFileLog) return;
        AppendToLog(message);
    }

    private void AppendToLog(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(_logFilePath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures
        }
    }
}


