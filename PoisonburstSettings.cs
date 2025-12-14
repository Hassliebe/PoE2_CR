using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace PoisonburstPathfinder;

public class PoisonburstSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    public ToggleNode AimAssistMode { get; set; } = new(false);
    public AimAssistSettings AimAssist { get; set; } = new();

    public GeneralSettings General { get; set; } = new();
    [IgnoreMenu] // Legacy; superseded by SkillRules editor
    public OffenseSettings Offense { get; set; } = new();
    public FlaskSettings Flasks { get; set; } = new();
    public SkillRulesSettings SkillRules { get; set; } = new();
    public DebugSettings Debug { get; set; } = new();
}

[Submenu(CollapsedByDefault = false)]
public class AimAssistSettings
{
    public RangeNode<int> EngageRange { get; set; } = new(75, 10, 200);
    public HotkeyNodeV2 AimKey { get; set; } = new(Keys.None);          // hold-to-aim
    public HotkeyNodeV2 AimToggleKey { get; set; } = new(Keys.None);    // toggle aim on/off
}

[Submenu(CollapsedByDefault = false)]
public class GeneralSettings
{
    public ToggleNode RunInTown { get; set; } = new(false);
    public ToggleNode RequireGameFocus { get; set; } = new(true);
    public ToggleNode PauseOnEscapeState { get; set; } = new(true);
    public ToggleNode PauseWhenPanelsOpen { get; set; } = new(true);
    public ToggleNode PauseWhenChatOpen { get; set; } = new(true);
}

[Submenu(CollapsedByDefault = false)]
public class OffenseSettings
{
    public ToggleNode EnablePrimary { get; set; } = new(true);
    public HotkeyNodeV2 PrimarySkillKey { get; set; } = new(Keys.Space);
    public RangeNode<int> EngageRange { get; set; } = new(75, 10, 150);
    public RangeNode<int> MinMonsterCount { get; set; } = new(1, 1, 15);
    public RangeNode<int> CastIntervalMs { get; set; } = new(175, 50, 2000);
}

[Submenu(CollapsedByDefault = false)]
public class FlaskSettings
{
    public ToggleNode EnableLifeFlask { get; set; } = new(false);
    public HotkeyNodeV2 LifeFlaskKey { get; set; } = new(Keys.None);
    public RangeNode<int> LifeThresholdPct { get; set; } = new(55, 1, 100);
    public RangeNode<int> LifeFlaskCooldownMs { get; set; } = new(1200, 250, 8000);

    public ToggleNode EnableManaFlask { get; set; } = new(false);
    public HotkeyNodeV2 ManaFlaskKey { get; set; } = new(Keys.None);
    public RangeNode<int> ManaThresholdPct { get; set; } = new(30, 1, 100);
    public RangeNode<int> ManaFlaskCooldownMs { get; set; } = new(1200, 250, 8000);
}

[Submenu(CollapsedByDefault = false)]
public class SkillRulesSettings
{
    public ToggleNode Enable { get; set; } = new(true);
    public RangeNode<int> GlobalCooldownMs { get; set; } = new(150, 50, 1000);
    [Menu("Open skill editor")]
    public ButtonNode ToggleEditorWindow { get; set; } = new();
    public ToggleNode ShowEditorWindow { get; set; } = new(true);
    public List<SkillRule> Rules { get; set; } = new();
}

public class SkillRule
{
    public string SkillId { get; set; } = string.Empty;
    public string SkillName { get; set; } = string.Empty;
    public Keys Key { get; set; } = Keys.None;
    public SkillCondition Condition { get; set; } = new();
}

public class SkillCondition
{
    public int MinMonstersInRange { get; set; } = 1;
    public int Range { get; set; } = 60;
    public int MinCooldownMs { get; set; } = 0;
    public MonsterRarityFilter RarityFilter { get; set; } = MonsterRarityFilter.Any;
    public bool RequireCursorNearby { get; set; } = false;
    public int CursorRange { get; set; } = 120;
    public bool OnlyIfBossPresent { get; set; } = false;
    public int MinLifePercent { get; set; } = 0;
    public bool SkipIfDeployedObjectNearby { get; set; } = false;
    public int DeployedObjectRange { get; set; } = 120;
    public List<string> FallbackEntityPathContains { get; set; } = new();
}

public enum MonsterRarityFilter
{
    Any,
    MagicOrHigher,
    RareOrHigher,
    UniqueOnly
}

[Submenu(CollapsedByDefault = true)]
public class DebugSettings
{
    public ToggleNode ShowOverlay { get; set; } = new(true);
    public RangeNode<int> OverlayX { get; set; } = new(20, 0, 4000);
    public RangeNode<int> OverlayY { get; set; } = new(20, 0, 4000);
    public ColorNode OverlayColor { get; set; } = Color.LawnGreen;
    public ToggleNode ShowCounts { get; set; } = new(true);
    public ToggleNode ShowTargetDetails { get; set; } = new(true);

    public ToggleNode EnableWindow { get; set; } = new(true);
    public HotkeyNodeV2 ToggleWindowKey { get; set; } = new(Keys.F8);
    public ToggleNode EnableFileLog { get; set; } = new(false);
    public RangeNode<int> LogLevel { get; set; } = new(1, 0, 3);
    public ToggleNode LogRotation { get; set; } = new(false);
}


