using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 键位绑定（2026-11-11）。玩家在「开始界面 → 按键设置」里改，默认值就是原来的那套。
///
/// 三条约定：
///   1. **鼠标键不参与改键**（左键开火 / 右键开镜写死），只改键盘；
///   2. 同一个键不能绑两个动作 —— <see cref="TrySet"/> 会返回冲突的动作名，界面据此拒绝；
///   3. 存 PlayerPrefs，重开游戏保留。
///
/// 移动轴原本走 `Input.GetAxis("Vertical"/"Horizontal")`（旧 Input Manager 的轴，
/// 运行时改不了），所以这里改成读键自己算 -1~1，才能支持 WASD 改键。
/// </summary>
public static class Bindings
{
    public enum Act
    {
        Forward, Back, Left, Right,
        Fire, Scope, FreeLook, SwitchWeapon, MgMode,
        LockOn, Shop, Pause, Garage, Decoy,
        LevelFlight,
        // 2026-11-17：灵魂出窍（没车且买不起车时升空观战）。
        // 注意这个键**在游戏内 HUD 上也会显示** —— 改了键，提示文字跟着变
        // （见 SoulCamera.HintText 与 MatchHUD.DrawSoulHint）。
        Soul,

        // 2026-11-18 深夜（用户要求"把灵魂模式的按键设置也加入按键设置里面"）：
        // 灵魂视角的飞行键单独成一组。默认值与驾驶键同键（WASD/Space/Shift/Alt），
        // **允许同键** —— 灵魂模式里车是停着的，两组键物理上互斥，不会打架。
        // 玩家也可以把它们改成别的键（例如方向键），改键时只与灵魂组内其它键判冲突。
        SoulForward, SoulBack, SoulLeft, SoulRight,
        SoulUp, SoulDown, SoulBoost,

        // 注意：2026-11-12 已删掉 Restart（重开一局）—— 改成按 P 暂停后在面板里选"是否重新开始"。
        // PlayerPrefs 的键名是 "Tank3D.Key." + 枚举名，不依赖枚举整数值，所以删掉是安全的。
    }

    /// <summary>驾驶移动键（陆/海/空通用）。</summary>
    public static bool IsDriveMove(Act a)
        => a == Act.Forward || a == Act.Back || a == Act.Left || a == Act.Right;

    /// <summary>灵魂视角的飞行键。</summary>
    public static bool IsSoulMove(Act a)
        => a == Act.SoulForward || a == Act.SoulBack || a == Act.SoulLeft || a == Act.SoulRight
        || a == Act.SoulUp || a == Act.SoulDown || a == Act.SoulBoost;

    public struct Entry
    {
        public Act act;
        public string label;
        public KeyCode def;
    }

    /// <summary>顺序即界面上的显示顺序。</summary>
    public static readonly Entry[] Table =
    {
        new Entry { act = Act.Forward,      label = "前进",       def = KeyCode.W },
        new Entry { act = Act.Back,         label = "后退",       def = KeyCode.S },
        new Entry { act = Act.Left,         label = "左转",       def = KeyCode.A },
        new Entry { act = Act.Right,        label = "右转",       def = KeyCode.D },
        new Entry { act = Act.Fire,         label = "开火（左键也可）", def = KeyCode.Space },
        new Entry { act = Act.Scope,        label = "开镜 / 拉近",  def = KeyCode.LeftShift },
        new Entry { act = Act.FreeLook,     label = "自由视角",     def = KeyCode.C },
        new Entry { act = Act.SwitchWeapon, label = "切换武器",     def = KeyCode.Q },
        new Entry { act = Act.MgMode,       label = "机枪模式",     def = KeyCode.V },
        new Entry { act = Act.LockOn,       label = "锁定目标",     def = KeyCode.LeftControl },
        new Entry { act = Act.Shop,         label = "补给商店",     def = KeyCode.Tab },
        new Entry { act = Act.Pause,        label = "暂停",        def = KeyCode.P },
        new Entry { act = Act.Garage,       label = "回车库换车",    def = KeyCode.G },
        // 2026-11-12 用户要求：删掉 R（重开一局）—— 改成按 P 暂停后在面板里选"是否重新开始"。
        new Entry { act = Act.Decoy,        label = "干扰弹（船）",  def = KeyCode.X },
        // 2026-11-12：飞机姿态放开成 360° 自由之后，需要一条"倒飞失控时救命"的键。
        // 按住 = 机翼水平 + 机头压回水平，松手恢复自由操控。
        new Entry { act = Act.LevelFlight,  label = "一键改平（飞机）", def = KeyCode.F },
        // 2026-11-17：灵魂出窍 / 观战（只在"没车且买不起车"时可用）
        new Entry { act = Act.Soul,         label = "灵魂出窍（观战）", def = KeyCode.O },

        // 2026-11-18 深夜：灵魂视角的飞行键也进按键设置（用户要求）。
        // 默认和驾驶键同键，界面上写清"复用驾驶键"，玩家想改就改。
        new Entry { act = Act.SoulForward,  label = "灵魂视角：前进",   def = KeyCode.W },
        new Entry { act = Act.SoulBack,     label = "灵魂视角：后退",   def = KeyCode.S },
        new Entry { act = Act.SoulLeft,     label = "灵魂视角：左移",   def = KeyCode.A },
        new Entry { act = Act.SoulRight,    label = "灵魂视角：右移",   def = KeyCode.D },
        new Entry { act = Act.SoulUp,       label = "灵魂视角：上升",   def = KeyCode.Space },
        new Entry { act = Act.SoulDown,     label = "灵魂视角：下降",   def = KeyCode.LeftShift },
        new Entry { act = Act.SoulBoost,    label = "灵魂视角：加速",   def = KeyCode.LeftAlt },
    };

    static Dictionary<Act, KeyCode> _map;
    const string K = "Tank3D.Key.";

    static void Ensure()
    {
        if (_map != null) return;
        _map = new Dictionary<Act, KeyCode>();
        foreach (var e in Table)
            _map[e.act] = (KeyCode)PlayerPrefs.GetInt(K + e.act, (int)e.def);
    }

    public static KeyCode Get(Act a)
    {
        Ensure();
        return _map.TryGetValue(a, out var k) ? k : KeyCode.None;
    }

    public static string LabelOf(Act a)
    {
        foreach (var e in Table) if (e.act == a) return e.label;
        return a.ToString();
    }

    /// <summary>改键。撞到别的动作就返回 false，conflict 带出被占用的那个动作。</summary>
    public static bool TrySet(Act a, KeyCode k, out Act conflict)
    {
        Ensure();
        conflict = Act.Forward;
        if (k == KeyCode.None) return false;

        foreach (var e in Table)
        {
            if (e.act == a) continue;
            // 2026-11-18：灵魂飞行键与驾驶键**允许同键**。两组键互斥生效
            // （没车时车不动、有车时不进灵魂视角），同键不会打架，
            // 而默认值本来就故意设成一样（WASD/Space/Shift/Alt），不放过就没法初始化。
            if ((IsDriveMove(a) && IsSoulMove(e.act)) || (IsSoulMove(a) && IsDriveMove(e.act))) continue;
            if (_map[e.act] == k) { conflict = e.act; return false; }
        }
        _map[a] = k;
        PlayerPrefs.SetInt(K + a, (int)k);
        PlayerPrefs.Save();
        return true;
    }

    /// <summary>
    /// 键名的**界面显示名**（`LeftShift` → `Shift`，`Space` → `Space`）。
    /// HUD 提示文字必须走这里 —— 否则玩家把键改掉了，提示还在写旧键名（2026-11-18 用户要求）。
    /// </summary>
    public static string Name(KeyCode k)
    {
        switch (k)
        {
            case KeyCode.LeftShift:
            case KeyCode.RightShift: return "Shift";
            case KeyCode.LeftAlt:
            case KeyCode.RightAlt: return "Alt";
            case KeyCode.LeftControl:
            case KeyCode.RightControl: return "Ctrl";
            case KeyCode.Space: return "Space";
            case KeyCode.UpArrow: return "↑";
            case KeyCode.DownArrow: return "↓";
            case KeyCode.LeftArrow: return "←";
            case KeyCode.RightArrow: return "→";
            case KeyCode.Return: return "Enter";
            case KeyCode.Tab: return "Tab";
            case KeyCode.Escape: return "Esc";
            default: return k.ToString();
        }
    }

    /// <summary>某个动作当前键的显示名（HUD 提示直接用）。</summary>
    public static string NameOf(Act a) => Name(Get(a));

    public static void ResetAll()
    {
        Ensure();
        foreach (var e in Table)
        {
            _map[e.act] = e.def;
            PlayerPrefs.SetInt(K + e.act, (int)e.def);
        }
        PlayerPrefs.Save();
    }

    /// <summary>玩家没改过任何键（用来在界面上提示"当前是默认键位"）。</summary>
    public static bool IsDefault()
    {
        Ensure();
        foreach (var e in Table) if (_map[e.act] != e.def) return false;
        return true;
    }

    // ---------------- 查询 ----------------

    public static bool Down(Act a) => Input.GetKeyDown(Get(a));
    public static bool Held(Act a) => Input.GetKey(Get(a));

    /// <summary>前进/后退轴：-1..1。</summary>
    public static float AxisV
        => (Held(Act.Forward) ? 1f : 0f) - (Held(Act.Back) ? 1f : 0f);

    /// <summary>左右轴：-1..1（右为正，和 GetAxis("Horizontal") 一致）。</summary>
    public static float AxisH
        => (Held(Act.Right) ? 1f : 0f) - (Held(Act.Left) ? 1f : 0f);

    /// <summary>开火：绑定的键 **或** 鼠标左键。</summary>
    public static bool FireHeld => Held(Act.Fire) || Input.GetMouseButton(0);

    /// <summary>开镜/拉近：绑定的键 **或** 鼠标右键。</summary>
    public static bool ScopeHeld => Held(Act.Scope) || Input.GetMouseButton(1);
}
