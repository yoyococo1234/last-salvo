using UnityEngine;

/// <summary>
/// 开局前「游戏调整」里的设置，全部走 PlayerPrefs 存档，重开游戏还在。
///
/// 为什么不用 MatchDef 资产存：那是 ScriptableObject，运行时改它会把改动写进 Assets（编辑器里
/// 会变成脏资产、build 里又不持久）。**玩家设置一律走这里**，对局开始时再读。
///
/// 约定：
///   - 人数只算 AI，玩家自己另算一台；
///   - 海/空有解锁时间（开局只玩坦克），飞机另有"一开始就开放"的开关。
/// </summary>
public static class MatchSettings
{
    // ---------------- 对局 ----------------

    /// <summary>初始金币。</summary>
    public static int StartingGold = 600;

    /// <summary>
    /// 开局多少分钟后海/空载具才开放。0 = 一开始就全开。
    ///
    /// 2026-11-18 深夜：**删掉了原来的 `AirFromStart` 开关**。
    /// 用户截图指出「飞机开局就开放」那个开关和「空战解锁时间」是同一件事，
    /// 两个都摆着会互相打架（一个说开、一个说等 N 分钟）。现在**只留这一条**：
    /// 设 0 就是开局全开，设 N 就是 N 分钟后开放。
    /// </summary>
    public static int UnlockMinutes = 3;

    // ---------------- 敌我人数（只算 AI） ----------------
    //
    // 2026-11-19 用户：「双方满编 6/6/6/6/6 没必要，减少几个」→ 默认改成 **每类 4 台**。
    // 每方 = 炮艇4 + 轻4 + 主4 + 重4 + 空4 = **20 AI**，双方共 40 台（+ 你 = 41）。
    // 上限仍是 MaxPerType=6，玩家可以在「游戏调整」里自己加回去。

    public static int AllyShips = 4;
    public static int AllyLight = 4;
    public static int AllyMain = 4;
    public static int AllyHeavy = 4;
    public static int AllyAir = 4;

    public static int EnemyShips = 4;
    public static int EnemyLight = 4;
    public static int EnemyMain = 4;
    public static int EnemyHeavy = 4;
    public static int EnemyAir = 4;

    // ---------------- 积分（VP）上涨倍率 ----------------
    //
    // 2026-11-17 用户要求：「在游戏设置调整积分上涨倍率（当然得有上限下限）」。
    //
    // 为什么需要它：两局模拟发现 VP 速率方差极大 ——
    //   占 3 点 9.8 分钟就到 1500（太快）；
    //   各占 1 点 15 分钟只到 886（永远打不满）。
    // 让玩家自己调，就不用我猜一个"最优值"。
    //
    // 倍率直接乘在 `MatchManager.vpPerZoneSeconds` 的反比上：
    //   倍率 1.0 = 默认（每据点每 1 秒 +1 VP）
    //   倍率 2.0 = 涨得快一倍（到 1500 的时间减半）
    //   倍率 0.5 = 涨得慢一半
    public static float VpRateMul = DefaultVpRate;

    /// <summary>默认倍率：0.75（较慢）。见 Load() 里的迁移说明。</summary>
    public const float DefaultVpRate = 0.75f;

    /// <summary>倍率的可选档位（界面直接给这几档，避免玩家拖出奇怪的值）。</summary>
    public static readonly float[] VpRateSteps = { 0.5f, 0.75f, 1f, 1.5f, 2f, 3f };
    public static readonly string[] VpRateNames = { "慢", "较慢", "标准", "较快", "快", "极快" };

    public const float VpRateMin = 0.5f;
    public const float VpRateMax = 3f;

    // ---------------- AI 强度 ----------------

    /// <summary>AI 技能系数：0.55 很菜 / 0.75 偏弱（默认）/ 1.0 原版（几乎枪枪中）。</summary>
    public static float AiSkill = 0.75f;

    /// <summary>三档难度，界面上直接给这三个。</summary>
    public static readonly float[] SkillSteps = { 0.55f, 0.75f };
    public static readonly string[] SkillNames = { "轻松", "普通" };
    // 2026-11-18 深夜：用户要求删掉「原版（很凶）」那一档（1.0）—— 太难，没人用。
    // ⚠️ 老存档 AiSkill=1.0 的情况在 Load() 里已降成 0.75。

    // ---------------- 人数上限 ----------------

    public const int MaxPerType = 6;

    // ---------------- 载具 id 映射 ----------------

    public const string IdShip = "sea_gunboat";
    public const string IdLight = "land_light";
    public const string IdMain = "land_main";
    public const string IdHeavy = "land_heavy";
    public const string IdFighter = "air_fighter";
    public const string IdAttacker = "air_attacker";

    /// <summary>某一方某类型几台（只算 AI）。</summary>
    public static int CountOf(int team, string id)
    {
        bool ally = team == 0;
        switch (id)
        {
            case IdShip: return ally ? AllyShips : EnemyShips;
            case IdLight: return ally ? AllyLight : EnemyLight;
            case IdMain: return ally ? AllyMain : EnemyMain;
            case IdHeavy: return ally ? AllyHeavy : EnemyHeavy;
            case IdFighter:
            case IdAttacker: return ally ? AllyAir : EnemyAir;
            default: return 0;
        }
    }

    /// <summary>某一方 AI 总台数。</summary>
    public static int AiCount(int team)
        => CountOf(team, IdShip) + CountOf(team, IdLight) + CountOf(team, IdMain)
         + CountOf(team, IdHeavy) + CountOf(team, IdFighter);

    /// <summary>
    /// 生成这一方 AI 的载具 id 队列（开局按这个顺序出场）。
    /// 顺序刻意打散：先主战、再重装、再海、再空，避免开局全挤在同一类。
    /// </summary>
    public static System.Collections.Generic.List<string> BuildAiRoster(int team)
    {
        var list = new System.Collections.Generic.List<string>();
        void Add(string id, int n) { for (int i = 0; i < n; i++) list.Add(id); }

        // 陆战打头（开局只有陆战能动），海空排后面
        Add(IdMain, CountOf(team, IdMain));
        Add(IdHeavy, CountOf(team, IdHeavy));
        Add(IdLight, CountOf(team, IdLight));
        Add(IdShip, CountOf(team, IdShip));

        // 2026-11-18 深夜（用户问"飞机上限多少、场上好像没这么多"时查出来的真 bug）：
        // 这里原来只 `Add(IdFighter, ...)` —— **攻击机一次都没生成过**，
        // 模型和数值（450 血 / 反坦克弹）做了却永远不上场。
        // 现在按槽位交替混编：0/2/4 战斗机、1/3/5 攻击机。
        int airN = CountOf(team, IdFighter);
        for (int i = 0; i < airN; i++) list.Add(i % 2 == 1 ? IdAttacker : IdFighter);
        return list;
    }

    // ---------------- 解锁 ----------------

    /// <summary>这一局已经打了多少秒（由 MatchManager 每帧写）。</summary>
    public static float MatchElapsed;

    /// <summary>
    /// 某个域的载具现在开不开放。
    /// 2026-11-11 用户澄清：**只要不开空战，海战照常开放**。
    /// 所以陆/海恒开放，只有飞机受开关和倒计时限制。
    /// </summary>
    public static bool DomainUnlocked(Domain d)
    {
        if (d != Domain.Air) return true;          // 陆战、海战：随时可用
        if (UnlockMinutes <= 0) return true;       // 倒计时设 0 = 立刻开放
        return MatchElapsed >= UnlockMinutes * 60f;
    }

    /// <summary>还要多久解锁（秒），已经开放返回 0。</summary>
    public static float UnlockIn(Domain d)
    {
        if (DomainUnlocked(d)) return 0f;
        return Mathf.Max(0f, UnlockMinutes * 60f - MatchElapsed);
    }

    // ---------------- 存读 ----------------

    const string K = "Tank3D.Settings.";

    public static void Load()
    {
        // 2026-11-17 经济系统：默认开局金币 200 → 600。
        // ⚠️ 老存档里可能还存着 200（PlayerPrefs 会盖掉代码默认值），
        // 所以这里加一个"低于 400 就按 600 起"的迁移，避免老玩家一直用旧值。
        StartingGold = PlayerPrefs.GetInt(K + "gold", 600);
        if (StartingGold < 400) StartingGold = 600;
        // 2026-11-18 深夜：原来那个独立的「飞机开局就开放」开关删掉了，语义并进 UnlockMinutes。
        // 老存档里如果存着 airFromStart=1，这里**迁移成 UnlockMinutes=0**（等价），
        // 否则玩家会变成"飞机永远不开放"（开关没了、分钟数还在 3）。
        bool legacyAirFromStart = PlayerPrefs.GetInt(K + "airFromStart", 0) != 0;
        UnlockMinutes = PlayerPrefs.GetInt(K + "unlockMin", 3);
        if (legacyAirFromStart) UnlockMinutes = 0;

        // 2026-11-19：默认编制改成每类 4 台（原来代码默认是 1/0/2/1/1，出货存档是 6/6/6/6/6）。
        AllyShips = PlayerPrefs.GetInt(K + "aShip", 4);
        AllyLight = PlayerPrefs.GetInt(K + "aLight", 4);
        AllyMain = PlayerPrefs.GetInt(K + "aMain", 4);
        AllyHeavy = PlayerPrefs.GetInt(K + "aHeavy", 4);
        AllyAir = PlayerPrefs.GetInt(K + "aAir", 4);

        EnemyShips = PlayerPrefs.GetInt(K + "eShip", 4);
        EnemyLight = PlayerPrefs.GetInt(K + "eLight", 4);
        EnemyMain = PlayerPrefs.GetInt(K + "eMain", 4);
        EnemyHeavy = PlayerPrefs.GetInt(K + "eHeavy", 4);
        EnemyAir = PlayerPrefs.GetInt(K + "eAir", 4);

        // 2026-11-19：老存档里那种"双方全 6 满编"一次性降成 4/类。
        // 只认"10 个值全是 6"这一个精确状态，玩家自己调过的组合一律不动。
        // ⚠️ 必须把新值**写回 PlayerPrefs**：只改内存的话，标记位已经是 1、
        //    下次启动又会从存档读回 6，等于迁移没做（第一次写漏了，11-19 打包前抓到）。
        if (PlayerPrefs.GetInt(K + "lineupMigrated", 0) == 0)
        {
            bool allSix = AllyShips == 6 && AllyLight == 6 && AllyMain == 6 && AllyHeavy == 6 && AllyAir == 6
                       && EnemyShips == 6 && EnemyLight == 6 && EnemyMain == 6 && EnemyHeavy == 6 && EnemyAir == 6;
            if (allSix)
            {
                AllyShips = AllyLight = AllyMain = AllyHeavy = AllyAir = 4;
                EnemyShips = EnemyLight = EnemyMain = EnemyHeavy = EnemyAir = 4;
                foreach (var key in new[] { "aShip", "aLight", "aMain", "aHeavy", "aAir",
                                            "eShip", "eLight", "eMain", "eHeavy", "eAir" })
                    PlayerPrefs.SetInt(K + key, 4);
                PlayerPrefs.Save();
            }
            PlayerPrefs.SetInt(K + "lineupMigrated", 1);
            PlayerPrefs.Save();
        }

        AiSkill = PlayerPrefs.GetFloat(K + "ai", 0.75f);
        // 2026-11-18 深夜：AI 强度删掉了最凶那档（1.0「原版（很凶）」），只留 轻松 0.55 / 普通 0.75。
        // 老存档要是选过 1.0，界面上两档都不会亮 —— 这里降成 0.75。
        if (AiSkill > 0.85f) AiSkill = 0.75f;

        // 2026-11-17 20:40：积分上涨倍率默认值 1.00（标准）→ **0.75（较慢）**。
        //
        // 原因：1.00 时占满 3 点只要 8.3 分钟就赢 ——
        // 实测一局敌方早早占满 3 点，我方连翻盘的时间都没有，9:47 就结束了。
        // 调到 0.75 后：
        //   占 3 点 → 11.1 分钟到 1500（多给落后方近 3 分钟）
        //   占 2 点 → 15 分钟只到 1350，打满按分数判（我方 1350 : 对方 675 稳赢）
        // 也就是说"必须占满 3 点才能提前赢"，占 2 点就得靠时间到比分数。
        //
        // 迁移：老存档里存着旧默认 1.00 的，自动换成新默认；
        // 玩家自己手动调过别的值（≠1.00）就保留不动。用一次性标记位，不会每次启动都重置。
        if (PlayerPrefs.GetInt(K + "vpRateMigrated", 0) == 0)
        {
            float old = PlayerPrefs.GetFloat(K + "vpRate", DefaultVpRate);
            if (Mathf.Abs(old - 1f) < 0.001f) old = DefaultVpRate;
            PlayerPrefs.SetFloat(K + "vpRate", old);
            PlayerPrefs.SetInt(K + "vpRateMigrated", 1);
        }
        VpRateMul = PlayerPrefs.GetFloat(K + "vpRate", DefaultVpRate);

        Clamp();
    }

    public static void Save()
    {
        Clamp();
        PlayerPrefs.SetInt(K + "gold", StartingGold);
        PlayerPrefs.SetInt(K + "unlockMin", UnlockMinutes);   // 2026-11-18：airFromStart 已删，只存这一条

        PlayerPrefs.SetInt(K + "aShip", AllyShips);
        PlayerPrefs.SetInt(K + "aLight", AllyLight);
        PlayerPrefs.SetInt(K + "aMain", AllyMain);
        PlayerPrefs.SetInt(K + "aHeavy", AllyHeavy);
        PlayerPrefs.SetInt(K + "aAir", AllyAir);

        PlayerPrefs.SetInt(K + "eShip", EnemyShips);
        PlayerPrefs.SetInt(K + "eLight", EnemyLight);
        PlayerPrefs.SetInt(K + "eMain", EnemyMain);
        PlayerPrefs.SetInt(K + "eHeavy", EnemyHeavy);
        PlayerPrefs.SetInt(K + "eAir", EnemyAir);

        PlayerPrefs.SetFloat(K + "ai", AiSkill);
        PlayerPrefs.SetFloat(K + "vpRate", VpRateMul);
        PlayerPrefs.Save();
    }

    public static void ResetAll()
    {
        StartingGold = 600; UnlockMinutes = 3;
        // 2026-11-19：重置也回到"每类 4 台"（每方 20 AI），不再是 1/0/2/1/1。
        AllyShips = 4; AllyLight = 4; AllyMain = 4; AllyHeavy = 4; AllyAir = 4;
        EnemyShips = 4; EnemyLight = 4; EnemyMain = 4; EnemyHeavy = 4; EnemyAir = 4;
        AiSkill = 0.75f;
        VpRateMul = DefaultVpRate;
        Save();
    }

    static void Clamp()
    {
        StartingGold = Mathf.Clamp(StartingGold, 0, 20000);
        UnlockMinutes = Mathf.Clamp(UnlockMinutes, 0, 15);
        AllyShips = Mathf.Clamp(AllyShips, 0, MaxPerType);
        AllyLight = Mathf.Clamp(AllyLight, 0, MaxPerType);
        AllyMain = Mathf.Clamp(AllyMain, 0, MaxPerType);
        AllyHeavy = Mathf.Clamp(AllyHeavy, 0, MaxPerType);
        AllyAir = Mathf.Clamp(AllyAir, 0, MaxPerType);
        EnemyShips = Mathf.Clamp(EnemyShips, 0, MaxPerType);
        EnemyLight = Mathf.Clamp(EnemyLight, 0, MaxPerType);
        EnemyMain = Mathf.Clamp(EnemyMain, 0, MaxPerType);
        EnemyHeavy = Mathf.Clamp(EnemyHeavy, 0, MaxPerType);
        EnemyAir = Mathf.Clamp(EnemyAir, 0, MaxPerType);
        AiSkill = Mathf.Clamp(AiSkill, 0.3f, 1f);
        VpRateMul = Mathf.Clamp(VpRateMul, VpRateMin, VpRateMax);
    }
}
