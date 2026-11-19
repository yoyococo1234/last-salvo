using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

/// <summary>
/// 对局主控（5v5 · 每人 3 条命 · 个人经济）。
///
/// 规则：
///   · 双方各 5 个单位；你是我方 0 号，其余 4 个是我方 AI，敌方 5 个全是 AI
///   · **命数机制已删除（2026-11-17）**：复活只受金币约束，胜负只看 VP
///   · 经济独立：每人有自己的金币。买车花自己的钱，击毁敌人 +40% 车价进自己口袋
///   · 唯一共享的收入是资源点：占下来的点每 10 秒给**全队每人**发一份 incomePerTick
///   · 一方 5 个人全部出局 → 对方获胜；时间到则比剩余命数
/// </summary>
public class MatchManager : MonoBehaviour
{
    public static MatchManager Instance { get; private set; }

    [Header("数据（留空则自动从 Assets/Data 找默认资产）")]
    public MatchDef match;
    public BattlefieldDef battlefield;

    [Header("规则")]
    [Tooltip("无争夺时占下一个点要几秒")]
    public float captureSeconds = 6f;
    [Tooltip("你阵亡后多久弹出车库")]
    public float garageDelay = 2.5f;
    [Tooltip("AI 阵亡后多久重新出击")]
    public float aiRespawnDelay = 6f;
    [Tooltip("每队人数")]
    public int teamSize = 5;

    [Header("测试开关")]
    [Tooltip("不限命数：玩家可以无限次换车，对局也不会因为你没命而结束（调参/验收时用）")]
    public bool unlimitedLives = true;
    [Tooltip("无限金币：玩家金币恒定，随便换车（调参/验收时用）")]
    public bool unlimitedGold = true;
    [Tooltip("无限金币时的金币数")]
    public int debugGold = 999999;

    [Header("运行时状态（只读，给 HUD 用）")]
    public float timeLeft;
    public bool matchOver;
    public int winnerTeam = -1;
    public string resultText = "";

    /// <summary>双方击杀数（HUD 顶部显示）。</summary>
    public readonly int[] kills = new int[2];

    // ==================== 胜利点（VP）====================
    // 2026-11-17 按 Docs/经济与积分方案_2026-11-12.md 的 P0 落地：
    //   占点 +1 VP / 2 s / 每点；首次占领 +25；击杀 +10；被毁不扣分。
    //   先到 vpToWin 获胜；时间到比 VP；VP 同分才回退到比剩余命数。
    // 为什么不再只看命数：原来占点只给钱，胜负跟"占点"完全无关，
    // 玩家不知道该抢点还是该苟，占点成了纯刷钱。

    [Header("胜利点（VP）")]
    [Tooltip("先到这个 VP 的一方获胜")]
    public int vpToWin = 1500;
    [Tooltip("每个据点每几秒产 1 VP")]
    public float vpPerZoneSeconds = 1f;
    [Tooltip("首次占领（从非我方变成我方）的一次性 VP")]
    public int vpCaptureBonus = 25;
    [Tooltip("每击毁一台敌方载具的 VP")]
    public int vpPerKill = 10;

    /// <summary>双方 VP（HUD 顶部显示）。</summary>
    public readonly int[] vp = new int[2];

    float _vpTimer;

    /// <summary>当前双方 VP（只读，给 HUD / 结算用）。</summary>
    public int Vp0 => vp[0];
    public int Vp1 => vp[1];

    /// <summary>加 VP（自动封顶并检查胜负）。</summary>
    public void AddVp(int team, int amount)
    {
        if (matchOver || amount == 0) return;
        int t = Mathf.Clamp(team, 0, 1);
        vp[t] = Mathf.Max(0, vp[t] + amount);
        if (vp[t] >= vpToWin) EndMatch(t, $"胜利点达到 {vpToWin}");
    }

    public readonly List<UnitState> units = new List<UnitState>();
    public readonly List<ZoneState> zones = new List<ZoneState>();
    public readonly List<FeedEntry> feed = new List<FeedEntry>();

    public class FeedEntry
    {
        public string text;
        public float time;
    }

    UnitState _human;
    bool _humanDeployedOnce;      // 已经出击过至少一次（首次出击不扣命）
    readonly Dictionary<VehicleCombat, UnitState> _map = new Dictionary<VehicleCombat, UnitState>();
    readonly Dictionary<VehicleCombat, int> _cache = new Dictionary<VehicleCombat, int>();
    float _garageAt = -1f;
    bool _garageOpenForGold;      // 攒够钱后自动弹车库的"只弹一次"闸门（2026-11-18）
    int _pendingTradeIn;          // 按 G 挂账的旧车回收额（2026-11-18）
    float _incomeTimer;
    float _nextCache;

    // ---------------- 对外只读接口 ----------------

    public UnitState Human => _human;
    public int Gold => _human != null ? _human.gold : 0;

    /// <summary>
    /// 已经出击过至少一次（2026-11-18 晚）。
    /// 车库「退出」靠它区分两种语义：
    ///   · 还没出击（开局那次）→ 退出这一局回主菜单
    ///   · 中途没车 → 不许空手走（走了就没有入口回车库）
    /// </summary>
    public bool HasDeployed => _humanDeployedOnce;

    /// <summary>商店买东西：够钱就扣，返回是否成功。</summary>
    public bool TrySpend(int amount)
    {
        if (_human == null) return false;
        if (amount <= 0) return true;
        if (_human.gold < amount) return false;
        _human.gold -= amount;
        return true;
    }
    public int LivesLeft => _human != null ? _human.lives : 0;
    public VehicleCombat Player => _human != null ? _human.vehicle : null;
    public VehicleDef[] Roster => match != null ? match.roster : null;

    // ---------------- AI 经济（2026-11-17 新增）----------------
    // 以前 AI 有金币但一分不花（见 AIDifficulty 的注释）。这里补上三件事：
    //   ① 按载具反查它属于哪个 UnitState（拿钱包）
    //   ② AI 也能像玩家一样花钱（TrySpendFor）
    //   ③ 复活时能挑更贵的车（TryUpgradeDef）

    /// <summary>按场上的载具反查它的 UnitState（钱包 / 命数）。找不到返回 null。</summary>
    public UnitState UnitOf(VehicleCombat v)
        => (v != null && _map.TryGetValue(v, out var u)) ? u : null;

    /// <summary>
    /// 替**某个单位**花钱（不是玩家专属）。AI 买维修/补给/模块修复都走这里。
    /// 与玩家的 TrySpend 分开，是因为 TrySpend 写死了 _human。
    /// </summary>
    public bool TrySpendFor(UnitState u, int amount)
    {
        if (u == null) return false;
        if (amount <= 0) return true;
        if (u.gold < amount) return false;
        u.gold -= amount;
        return true;
    }

    /// <summary>
    /// 这个单位复活时能不能/该不该换一台更贵的车。
    /// 返回 null = 不换，沿用原来的。
    /// 只在 AIDifficulty 判定"该升级"且钱够时才返回，**不会动玩家的车**。
    /// </summary>
    public VehicleDef TryUpgradeDef(UnitState u, VehicleDef current, float goldMul)
    {
        if (u == null || current == null) return null;
        var r = Roster;
        if (r == null) return null;

        // 只在同域里挑（陆战别给换成船）
        VehicleDef best = null;
        foreach (var d in r)
        {
            if (d == null) continue;
            if (d.domain != current.domain) continue;
            if (d.price <= current.price) continue;                 // 只往贵的换
            if (!MatchSettings.DomainUnlocked(d.domain)) continue;   // 还没解锁的不能选
            if (best == null || d.price > best.price) best = d;
        }
        if (best == null) return null;

        // 要留出余量：钱够 车价 × goldMul 才换，避免换完就没钱复活
        if (u.gold < best.price * goldMul) return null;
        return best;
    }

    public int TeamLives(int team)
    {
        int n = 0;
        foreach (var u in units) if (u.team == team) n += Mathf.Max(0, u.lives);
        return n;
    }

    public int TeamAlive(int team)
    {
        int n = 0;
        foreach (var u in units) if (u.team == team && u.HasVehicle) n++;
        return n;
    }

    public int EnemiesLeft => TeamAlive(1);

    /// <summary>
    /// 最便宜那台车的**实际价格**（含复活/换车递增）。
    ///
    /// 2026-11-18 修复：原来用 `d.price` 原价，导致车库副标题永远显示"最便宜 320"，
    /// 玩家买到第 4 台时实际要 370，标题却还说 320，看着像"价格没涨"。
    /// </summary>
    public int CheapestPrice
    {
        get
        {
            int min = int.MaxValue;
            var r = Roster;
            if (r != null) foreach (var d in r) if (d != null) min = Mathf.Min(min, PriceFor(d));
            return min == int.MaxValue ? 0 : min;
        }
    }

    public bool CanAfford(VehicleDef def)
    {
        if (def == null || _human == null) return false;
        // 命数已删除（2026-11-17）：能不能出击只看钱。
        // ⚠️ 2026-11-17 傍晚用户明确要求：**删掉"最便宜的车免费"**。
        // 原来这里是 `if (_human.gold < CheapestPrice && def.price == CheapestPrice) return true;`
        // 意思是"一穷二白时最便宜那台白送"（轻坦 / 战斗机都是 160）。
        // 用户原话："战斗机和轻坦它是不该免费的。如果免费，我还要这个灵魂出窍干什么呢？"
        // 现在没钱就是没钱 —— 你得等占点收入攒够，这期间正好用灵魂出窍看战局。
        return _human.gold >= def.price;
    }

    /// <summary>命数显示文案。命数机制已删除，保留字段只为兼容旧 UI，恒返回 ∞。</summary>
    public string LivesText => "∞";

    // ---------------- 生命周期 ----------------

    void Awake()
    {
        Instance = this;
        LoadDefaults();
    }

    void LoadDefaults()
    {
#if UNITY_EDITOR
        if (match == null)
            match = UnityEditor.AssetDatabase.LoadAssetAtPath<MatchDef>("Assets/Data/Match/Match_Default.asset");
        if (battlefield == null)
            battlefield = UnityEditor.AssetDatabase.LoadAssetAtPath<BattlefieldDef>("Assets/Data/Battlefield/Battlefield_Default.asset");
#endif
        if (match == null) match = ScriptableObject.CreateInstance<MatchDef>();
        if (battlefield == null) battlefield = ScriptableObject.CreateInstance<BattlefieldDef>();
    }

    void Start()
    {
        timeLeft = match.matchTimeLimitMinutes * 60f;
        MatchSettings.MatchElapsed = 0f;                 // 2026-11-11：海/空解锁计时从 0 开始
        MatchHUD.ResetMapNorth();                        // 2026-11-12：小地图"北"= 本局开局的朝向
        VehicleAI.Skill = MatchSettings.AiSkill;         // AI 难度（游戏调整里选）

        zones.Clear();
        if (battlefield.zones != null)
            foreach (var z in battlefield.zones)
                if (z != null) zones.Add(new ZoneState { def = z });
        ZoneMarker.BuildAll(zones);

        BuildUnits();
        BattleEvents.VehicleDestroyed += OnVehicleDestroyed;

        // AI 立刻进场（你不用等他们）；海/空的单位要等解锁时间到了才出场
        foreach (var u in units)
        {
            if (u.isHuman) continue;

            var dom = DomainOf(u);
            float wait = MatchSettings.UnlockIn(dom);
            if (wait > 0f)
            {
                // 还没解锁：挂成"待出场"，由 UpdateRespawns 每拍检查解锁，到点补上且不扣命
                u.firstSpawnPending = true;
                u.respawnAt = -1f;
                continue;
            }
            SpawnUnit(u, PickDef(u));
        }

        int rosterCount = Roster != null ? Roster.Length : 0;
        Debug.Log($"[MatchManager] 自检 roster={rosterCount} zones={zones.Count} allyX={battlefield.allySpawnLineX:F0} " +
                  $"units={units.Count} gold={MatchSettings.StartingGold} vpToWin={vpToWin}");
        if (rosterCount == 0) Debug.LogError("[MatchManager] roster 为空 —— MatchDef 没序列化进场景");
        if (zones.Count == 0) Debug.LogError("[MatchManager] zones 为空 —— BattlefieldDef 没序列化进场景");

        int a = TeamCount(0), e = TeamCount(1);
        // 2026-11-18：AirFromStart 开关已删，空战开放时间只看 UnlockMinutes（0 = 开局全开）
        string unlockNote = MatchSettings.UnlockMinutes > 0
            ? $" · 空战 {MatchSettings.UnlockMinutes} 分钟后开放"
            : "";
        PushFeed($"我方 {a} 台 vs 敌方 {e} 台 · 先到 {vpToWin} 胜利点{unlockNote}");
    }

    void OnDestroy() => BattleEvents.VehicleDestroyed -= OnVehicleDestroyed;

    void BuildUnits()
    {
        units.Clear();
        _map.Clear();
        _human = null;

        // 2026-11-11：人数不再写死 5v5，改成「游戏调整」里按类型配（只算 AI，玩家另算一台）。
        for (int team = 0; team < 2; team++)
        {
            var ids = MatchSettings.BuildAiRoster(team);

            // 玩家自己占 team0 的 0 号位，车由车库选
            if (team == 0)
            {
                var me = new UnitState
                {
                    team = 0,
                    index = 0,
                    isHuman = true,
                    gold = MatchSettings.StartingGold,
                    lives = 999,   // 命数已废弃（2026-11-17），保留字段只为兼容
                };
                units.Add(me);
                _human = me;
                _humanDeployedOnce = false;      // 首次出击不扣命（见 DeployPlayer）
            }

            for (int i = 0; i < ids.Count; i++)
            {
                var u = new UnitState
                {
                    team = team,
                    index = i + (team == 0 ? 1 : 0),   // team0 的 AI 从 1 号开始编号，别和"你"撞号
                    isHuman = false,
                    gold = MatchSettings.StartingGold,
                    lives = 999,   // 命数已废弃（2026-11-17），保留字段只为兼容
                    prefId = ids[i],
                };
                units.Add(u);
            }
        }

        teamSize = Mathf.Max(TeamCount(0), TeamCount(1));
        Debug.Log($"[MatchManager] 人数配置：我方 {TeamCount(0)} 台 / 敌方 {TeamCount(1)} 台（含你）");
    }

    /// <summary>某一方总台数（含玩家）。</summary>
    int TeamCount(int team)
    {
        int n = 0;
        foreach (var u in units) if (u.team == team) n++;
        return n;
    }

    // ---------------- 每帧 ----------------

    void Update()
    {
        if (!GameStartScreen.Started) return;

        if (matchOver)
        {
            // 2026-11-12 用户要求：删掉 R 键重开，改成按 P 暂停后在面板里选"是否重新开始"。
            return;
        }

        if (_garageAt > 0f && Time.unscaledTime >= _garageAt)
        {
            _garageAt = -1f;
            OpenGarageForRespawn();
        }

        // 2026-11-18：阵亡后买不起 → 没弹车库（在上面 return 了），玩家会留在战场上用灵魂出窍攒钱。
        // 攒够的那一刻要把车库补上，否则他没钱时没车库、有钱了也没车库，只能自己按 G。
        //
        // 2026-11-18 深夜用户报"死亡动画没了，死的一瞬间就进入商店"：
        // 这个分支原来**不等 garageDelay**，玩家一死、只要还买得起，下一帧就弹库，
        // 把 OnVehicleDestroyed 里排的 2.5 秒等待（`_garageAt`）彻底跳过，死亡爆炸/残骸全被盖住。
        // 加 `_garageAt <= 0f`：手上还有待执行的弹库排程时让路，等它到点自己弹。
        //
        // 2026-11-18 深夜再修（用户："还没打就这样了"，截图标题写着"载具被击毁 —— 重新选择"）：
        // **开局还没出击时**（`_humanDeployedOnce == false`，Player 也是 null）这个条件同样成立，
        // 于是玩家在开局车库里按 Esc 关掉车库的下一帧就被弹回来，标题变成"载具被击毁"。
        // 加 `_humanDeployedOnce`：只有**真出击过**才谈得上"被击毁后重新选择"。
        if (Player == null && _humanDeployedOnce && !_garageOpenForGold && CanAffordCheapest && _garageAt <= 0f
            && (GarageScreen.Instance == null || !GarageScreen.Instance.IsOpen))
        {
            _garageOpenForGold = true;       // 只自动弹一次，别每帧抢
            OpenGarage("金币够了 —— 重新出击");
        }
        if (Player != null) _garageOpenForGold = false;    // 上车了就复位，下次阵亡还能触发

        // 2026-11-18 深夜用户报"这个界面按 G 回不去"：
        // G 原来**只**挂在 `PlayerTankController`（车身上的组件）里，没车时那个组件根本不存在，
        // 所以战场上空手按 G 完全没反应。这里补一条常驻兜底 —— 没车按 G 也能进车库。
        // 有车时仍走 PlayerTankController（那里会先挂账旧车回收），两条路互不重叠。
        if (Player == null && !matchOver && Bindings.Down(Bindings.Act.Garage)
            && (GarageScreen.Instance == null || !GarageScreen.Instance.IsOpen))
            OpenGarageManual();

        timeLeft -= Time.deltaTime;
        // 2026-11-11：海/空解锁计时（开局只玩坦克，到点才放海空）
        MatchSettings.MatchElapsed = Mathf.Max(0f, match.matchTimeLimitMinutes * 60f - timeLeft);
        if (timeLeft <= 0f)
        {
            timeLeft = 0f;
            EndByLives("时间到");
            return;
        }

        UpdateZones(Time.deltaTime);
        UpdateVp(Time.deltaTime);
        UpdateIncome(Time.deltaTime);
        UpdateRespawns();
        CheckTeamWipe();

        // 无限金币（测试用）：每帧顶满，换车扣了也立刻补回来
        if (unlimitedGold && _human != null) _human.gold = debugGold;
    }

    void RefreshCache()
    {
        if (Time.time < _nextCache) return;
        _nextCache = Time.time + 0.4f;
        _cache.Clear();
        foreach (var v in FindObjectsByType<VehicleCombat>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (v != null) _cache[v] = v.Team;
    }

    // ---------------- 出击 / 复活 ----------------

    VehicleDef PickDef(UnitState u)
    {
        // 2026-11-11：单位开什么车由「游戏调整」里的人数配置决定（BuildUnits 写进 prefId）。
        // 只有没配的情况才退回旧的平均分配逻辑。
        string id = u.prefId;
        if (string.IsNullOrEmpty(id))
        {
            string[] ids = u.team == 0
                ? new[] { "land_main", "land_heavy", "sea_gunboat", "air_fighter" }
                : new[] { "land_main", "land_heavy", "sea_gunboat", "air_fighter", "air_attacker" };
            id = ids[u.index % ids.Length];
        }

        var r = Roster;
        if (r != null) foreach (var d in r) if (d != null && d.id == id) return d;
        return r != null && r.Length > 0 ? r[0] : null;
    }

    /// <summary>这个单位的车属于哪个域（用来判断海/空有没有解锁）。</summary>
    static Domain DomainOf(UnitState u)
    {
        switch (u.prefId)
        {
            case MatchSettings.IdShip: return Domain.Sea;
            case MatchSettings.IdFighter:
            case MatchSettings.IdAttacker: return Domain.Air;
            default: return Domain.Land;
        }
    }

    VehicleCombat SpawnUnit(UnitState u, VehicleDef def)
    {
        if (u == null || def == null) return null;

        Vector3 pos = FindSpawnPoint(u, def);
        float yaw = u.team == 0 ? 90f : -90f;

        var v = VehicleFactory.Spawn(def, u.team, pos, yaw, u.isHuman);
        if (v == null) return null;

        u.vehicle = v;
        u.lastDef = def;
        u.respawnAt = -1f;
        _map[v] = u;

        if (u.isHuman)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                var cf = cam.GetComponent<CameraFollow>();
                if (cf != null) { cf.target = v.transform; cf.ResetToBehindTarget(); }
            }
        }
        return v;
    }

    /// <summary>开局：进车库选第一台车。</summary>
    public void OpenGarageFirst()
    {
        OpenGarage("选择你的载具");
    }

    void OpenGarageForRespawn()
    {
        if (matchOver) return;

        // 2026-11-18 修死锁：**买不起就别弹车库**。
        // 车库一开就 `Time.timeScale = 0`，占点收入停摆、金币永远不涨；
        // 而车库开着时灵魂出窍又被 CanUse() 排除 → 玩家卡在车库里出不来（只能退出整局）。
        // 正确行为：买不起 → 不进车库，留在大地图上，灵魂出窍自动升空观战攒钱；
        //           攒够了 CanAffordCheapest 变 true，SoulCamera 自动退出、这里再弹车库。
        if (!CanAffordCheapest) return;

        // 2026-11-17 傍晚用户要求：**不再自动补钱**。
        // 原来这里会"钱不够最便宜那台时补一笔刚好够的"（PushFeed「复活补给 +N 金」），
        // 等于死了就白送一台车。用户原话：
        //   "战斗机和这个轻坦它是不该免费的。如果免费，我还要这个灵魂出窍干什么呢？"
        // 现在没钱就进不了车库买车 —— 你会在战场外面等，同时用灵魂出窍看战局攒钱。
        OpenGarage("载具被击毁 —— 重新选择");
    }

    /// <summary>
    /// 按 G 回车库（2026-11-18 用户重新定义语义）。
    ///
    /// 用户原话："按 G 键之后，其他的都不变，但这个时候事实上'我'已经死了，也就可以进入灵魂状态"。
    /// 后来又强调："50% 的价钱依然要加入金币啊"。
    ///
    /// 所以正确做法是**挂账**，不是当场销毁：
    ///   ① 按 G 时先把旧车的 50% 折价记到 <see cref="_pendingTradeIn"/>（**先不销毁车**，
    ///      这样车库卡片上「旧车 +70」照常显示，价格数字和以前一模一样）
    ///   ② 玩家点了新车 → 旧车这时才销毁，回收金进钱包，再按正常价买
    ///   ③ 玩家按「退出」不买了 → 旧车照旧销毁、回收金照样进钱包，人变"没车"状态
    ///      （这就是用户要的"按 G 就等于我死了"），此时按 O 就能进灵魂视角
    ///
    /// 为什么不能像之前那样"按 G 立刻销毁"：车一没，`TradeInValue` 就是 0，
    /// 卡片上的「旧车 +70」会消失、价格也变回全价，跟以前不一样了。
    /// </summary>
    public void OpenGarageForSwap()
    {
        if (matchOver) return;
        var g = GarageScreen.Instance;
        if (g == null) return;
        if (g.IsOpen) return;                      // 已经在车库里就别重复开

        // 挂账：记住旧车值多少钱（等真正放弃/换车时才入账）
        _pendingTradeIn = Player != null ? TradeInValue : 0;

        OpenGarage("换载具");
    }

    // 2026-11-18 深夜（用户对着「你的金币 630 · 最便宜 147」说"这两行写了有鸟用"）：
    // 车库副标题整个删掉了。理由：
    //   · 金币格已经在左上角显眼位置写着「金币 630」（有旧车折价时还会显示"可用金币 X / 630+70"）
    //   · 每张车卡右下角写着「还差 N 金」，买不买得起一目了然
    //   · 副标题再写一遍只是重复，而且之前还压着标题（见 GarageScreen.OnGUI 的注释）
    // 现在所有 OpenGarage 调用都不传 subtitle。

    /// <summary>
    /// 真正放弃当前载具（2026-11-18）。
    /// 按 G 进来后，玩家点「退出」不买车、或者直接按 O 去观战，都走这里。
    /// 效果 = 环境致死（不给敌人奖励/VP）+ 旧车 50% 折价进钱包。
    /// </summary>
    public void AbandonCurrentVehicle()
    {
        var v = Player;
        if (v == null || matchOver) return;

        int tradeIn = _pendingTradeIn > 0 ? _pendingTradeIn : TradeInValue;
        v.DestroyByEnvironment();
        if (_human != null)
        {
            _human.vehicle = null;
            if (tradeIn > 0) _human.gold += tradeIn;
        }
        _pendingTradeIn = 0;
        _garageAt = -1f;                     // 别让 2.5 秒后那个自动弹库再插一脚
        if (tradeIn > 0) PushFeed($"旧车回收 +{tradeIn} 金");
    }

    /// <summary>
    /// 挂账中的旧车回收额。>0 时说明"按 G 进来了但还没决定买不买"。
    /// 车库的卡片显示、退出按钮、灵魂视角都读它。
    /// </summary>
    public int PendingTradeIn => _pendingTradeIn;

    /// <summary>
    /// 取消换车（2026-11-18 深夜）：玩家按 Esc 或者直接关掉车库。
    /// 车没卖，所以把按 G 时挂的那笔旧车回收账清掉，不然它会一直挂在 <see cref="TradeInValue"/> 上。
    /// </summary>
    public void CancelGarageSwap() => _pendingTradeIn = 0;

    /// <summary>
    /// 手动打开车库（2026-11-18：给"灵魂视角按 O/Esc 退回来""没车按 G"用）。
    /// 与 <see cref="OpenGarageForRespawn"/> 不同：这里不判买不买得起 ——
    /// 玩家主动想看车库就给他看，顶多看到"金币不足"，退出键能回战场。
    ///
    /// 2026-11-18 深夜：**玩家主动开的车库一律不自动升空**。
    /// 原来只有 `fromSoul` 才关掉倒计时，结果"没车按 G 进车库"1.5 秒又被弹回天上 ——
    /// 用户原话就是"这个界面按 G 回不去"。自动升空只留给**系统自己弹库**那条路。
    /// </summary>
    public void OpenGarageManual()
    {
        if (matchOver) return;
        var g = GarageScreen.Instance;
        if (g == null) return;
        if (g.IsOpen) return;
        OpenGarage(Player == null ? "选择你的载具" : "换载具", "", allowAutoSoul: false);
    }

    void OpenGarage(string title, string subtitle = "", bool allowAutoSoul = true)
    {
        var g = GarageScreen.Instance;
        if (g == null) { Debug.LogWarning("[MatchManager] 场景里没有 GarageScreen"); return; }
        g.Open(title, subtitle, allowAutoSoul);
    }

    /// <summary>
    /// 你这一台车现在要花多少钱（含复活递增 + 换车涨价）。
    ///
    /// ============ 2026-11-17 经济系统（用户定稿）============
    ///   ① 复活递增：车价 × respawnGrowth^(死亡次数)，respawnGrowth = **1.05**
    ///   ② 换车递增：车价 × swapGrowth^(换车次数)
    ///   ③ **总倍率封顶 1.5**（用户 2026-11-17 18:45："死亡的换车最大倍率为 1.5"）
    ///      —— 不管死多少次、换多少次，车价最多只涨到原价的 150%
    ///   ④ **换车回收 50%**：你手上还有一台完好的车时按 G 换车，
    ///      旧车按「它当初的售价 × 50%」折回你的钱包
    ///
    /// ⚠️ 2026-11-18 修经济漏洞：④ 原来是从**车价里直接扣**，然后 `Max(0, ...)`。
    /// 于是开炮艇（原价 320、回收 160）时，轻坦 140 − 160 被截成 **0 → 显示「免费」**，
    /// 身上只剩 10 金也能白拿一台车。用户明确说过"轻坦和战斗机不该免费"。
    /// 现在 ④ 改成**单独退款进钱包**，不参与车价计算 ——
    /// 车价永远 ≥ 原价 × 倍率（绝不会是 0），回收的钱进钱包后再参与"买不买得起"的判断。
    /// </summary>
    public int PriceFor(VehicleDef def)
    {
        if (def == null || _human == null) return 0;

        float mul = 1f;
        // ① 复活递增（只算"死过几次"，不含首次出击）
        if (match != null && _human.respawnCount > 0)
            mul *= Mathf.Pow(Mathf.Max(1f, match.respawnGrowth), _human.respawnCount);
        // ② 换车递增（单独也封顶 swapGrowthCap，防止极端值）
        if (match != null && _human.swapCount > 0)
            mul *= Mathf.Min(Mathf.Pow(Mathf.Max(1f, match.swapGrowth), _human.swapCount),
                             Mathf.Max(1f, match.swapGrowthCap));

        // ③ 总倍率封顶 1.5（用户定稿）
        mul = Mathf.Min(mul, MaxPriceMul);

        // 不再减 TradeInValue（见上面 ④ 的说明）
        return Mathf.Max(1, Mathf.RoundToInt(def.price * mul));
    }

    /// <summary>
    /// 换车时你手头实际能动用的钱 = 金币 + 旧车回收。
    /// 车库的"买得起"判断和 <see cref="DeployPlayer"/> 都用这个，
    /// 别再拿 Gold 直接和车价比 —— 那样会忽略回收，显示"金币不足"但其实是买得起的。
    /// </summary>
    public int SpendableGold => Gold + TradeInValue;

    /// <summary>
    /// 车价总倍率上限（复活递增 × 换车递增 合起来不超过这个）。
    /// 2026-11-17 用户："死亡的换车最大倍率为 1.5"。
    /// </summary>
    public const float MaxPriceMul = 1.5f;

    /// <summary>
    /// 当前可回收的旧车价值 = **当时实际买入价 × 50%**。
    ///
    /// 2026-11-18 用户明确要求："旧车的当时的购买价格的一半才是要标的价"。
    /// 以前用 `v.Def.price * 0.5f`（原价一半）—— 买船花了 353，却只退 160，玩家吃亏。
    /// 现在读 <see cref="UnitState.paidForCurrent"/>（DeployPlayer 里记的实际支出）。
    ///
    /// <see cref="_pendingTradeIn"/> 是"按 G 挂账"的副本：按 G 时车还没销毁，
    /// 但如果中途车没了（被击毁等），这笔钱不能凭空消失。
    /// </summary>
    public int TradeInValue
    {
        get
        {
            var v = Player;
            int live = 0;
            if (v != null && _human != null)
                live = Mathf.RoundToInt(_human.paidForCurrent * 0.5f);
            return Mathf.Max(live, _pendingTradeIn);
        }
    }

    /// <summary>
    /// 你现在**买得起**最便宜的那台车吗（含旧车回收）。
    ///
    /// 2026-11-18 修死锁时加的：阵亡后要不要弹车库，全看这个。
    /// 买不起就别弹 —— 弹了会 `Time.timeScale = 0`，占点收入停摆，
    /// 金币永远涨不上去，而车库开着时灵魂出窍又被禁用 → 玩家卡死在车库里出不来。
    ///
    /// ⚠️ 2026-11-18 修复：原来用 `_human.gold >= min`，**忽略了旧车回收**。
    /// 开炮艇时钱包 24 + 回收 160 = 实际可用 184，买得起 140 的轻坦，
    /// 但只看钱包 24 就判"买不起" → 按 O 会白白弃掉一辆好车。
    /// 现在统一用 <see cref="SpendableGold"/>（= 钱包 + 旧车回收）。
    /// </summary>
    public bool CanAffordCheapest
    {
        get
        {
            if (_human == null) return false;
            var r = Roster;
            if (r == null || r.Length == 0) return false;

            int min = int.MaxValue;
            foreach (var d in r)
            {
                if (d == null) continue;
                if (!MatchSettings.DomainUnlocked(d.domain)) continue;   // 还没解锁的域不算
                min = Mathf.Min(min, PriceFor(d));
            }
            return min != int.MaxValue && SpendableGold >= min;
        }
    }

    /// <summary>你在车库里点了一台车（花你自己的钱）。</summary>
    public void DeployPlayer(VehicleDef def)
    {
        if (def == null || matchOver || _human == null) return;

        int cost = PriceFor(def);                   // 正常车价（含复活/换车递增，不含回收抵扣）
        int tradeIn = TradeInValue;                 // 旧车回收（没车时为 0）

        // 2026-11-18 用户定稿的算法：**先把旧车折的钱加进钱包，再按正常价买**。
        //   gold += tradeIn;  gold -= cost;
        // 不是"从车价里扣回收"（那会把 140-160 算成 0 → 显示免费，还能白拿车）。
        if (_human.gold + tradeIn < cost) return;

        // 换车（场上有车）还是复活（场上没车）—— 影响递增计数
        bool swapping = _human.vehicle != null;

        if (tradeIn > 0) _human.gold += tradeIn;    // ① 旧车先折成钱进钱包
        _human.gold -= cost;                        // ② 再按正常价买新车
        if (_human.gold < 0) _human.gold = 0;
        _human.paidForCurrent = cost;               // ③ 记下这台车的实际买入价（回收按它的一半算）
        if (swapping) _human.swapCount++;
        _humanDeployedOnce = true;

        if (swapping)
        {
            _map.Remove(_human.vehicle);
            Destroy(_human.vehicle.gameObject);
            _human.vehicle = null;
        }

        _pendingTradeIn = 0;                        // 回收已经入账，清掉挂账别重复给

        SpawnUnit(_human, def);

        if (swapping && tradeIn > 0)
            PushFeed($"你 换装 {def.displayName}  -{cost}金（旧车回收 {tradeIn}）");
        else
            PushFeed($"你 部署 {def.displayName}  -{cost}金");

        if (GarageScreen.Instance != null) GarageScreen.Instance.Close();
    }

    void UpdateRespawns()
    {
        foreach (var u in units)
        {
            if (u.isHuman || u.vehicle != null) continue;

            // 2026-11-11：开局时海/空还没解锁的那批 —— 每拍检查一次解锁状态，
            // 到点就出场，而且**不扣命、不扣钱**（第一次出场不算复活）。
            // 注意这里必须在下面 respawnAt 判断之前：待出场单位的 respawnAt 是 -1。
            if (u.firstSpawnPending)
            {
                var d0 = u.lastDef != null ? u.lastDef : PickDef(u);
                if (d0 != null && !MatchSettings.DomainUnlocked(d0.domain)) continue;
                u.firstSpawnPending = false;
                u.respawnAt = -1f;
                if (SpawnUnit(u, d0) != null)
                    PushFeed($"{u.Label} 进入战场（{(d0 != null ? d0.displayName : "载具")}）");
                continue;
            }

            if (u.respawnAt < 0f || Time.time < u.respawnAt) continue;

            var def = u.lastDef != null ? u.lastDef : PickDef(u);

            // 2026-11-17：高难度 AI 会用攒下的钱**换一台更贵的车**（同域、只往贵换）。
            // 低难度不会（AIDifficulty.upgradesVehicle 为 false），保持"AI 一直开同一台"。
            var prof = AIDifficulty.Of(MatchSettings.AiSkill);
            if (prof.upgradesVehicle && def != null)
            {
                var better = TryUpgradeDef(u, def, prof.upgradeGoldMul);
                if (better != null)
                {
                    PushFeed($"{u.Label} 换装 {better.displayName}");
                    def = better;
                }
            }

            // 复活时海/空还没解锁就先等（不会发生：解锁是单向的，这里只是兜底）
            if (def != null && !MatchSettings.DomainUnlocked(def.domain))
            {
                u.respawnAt = Time.time + 2f;
                continue;
            }

            // 2026-11-17：AI 也走**复活递增**（和玩家同一套系数），
            // 否则"AI 死了永远原价复活、玩家越死越贵"会变成对玩家不公平。
            int cost = def != null ? def.price : 0;
            if (def != null && match != null && u.respawnCount > 0)
                cost = Mathf.RoundToInt(cost * Mathf.Pow(Mathf.Max(1f, match.respawnGrowth), u.respawnCount));

            if (u.gold < cost)
            {
                // AI 钱不够：攒钱，过几秒再试
                u.respawnAt = Time.time + 2f;
                continue;
            }

            u.gold -= cost;
            // 命数已删除（2026-11-17）：AI 只要有钱就能一直复活
            if (SpawnUnit(u, def) != null)
                PushFeed($"{u.Label} 重新出击  -{cost}金");
        }
    }

    // ---------------- 出生点 ----------------

    /// <summary>
    /// 这一台在出生线上该排第几（0~1 的比例）。
    ///
    /// 2026-11-18 深夜用户要求："尽量把 AI 队友的出生点铺展开，最好横着一字排开，
    /// 现在太挤了，注意不要撞上玩家。"
    ///
    /// 旧的起点是 `0.5 + (slot-2)*0.08`：slot ≥ 7 之后 t 就超出 [0.02, 0.98] 被跳过，
    /// 只能靠 ±i*0.006 的往复扫描慢慢挪回范围里 —— 满编 30 台时后半段全挤在 0.98 附近，
    /// 互相按 16 m 避让，看着就是一团。
    ///
    /// 现在按"同队同域的第 k 台 / 共 n 台"均匀铺在 [0.06, 0.94]：
    /// 出生线 1934 m，30 台 → 间距约 57 m，16 m 避让完全够；玩家也占自己的槽位，不会和 AI 重叠。
    /// </summary>
    /// <summary>
    /// 这一台在同类型里的排号（0~1）。
    ///
    /// 2026-11-19 用户明确口径："主战均匀铺展在全图的 z，轻坦也是，飞机也是，
    /// 而重装主要在城区；并且他们都主要在出生那条线上，只能微弱改变前后，不能直接贴脸到据点。"
    ///
    /// 所以编号改成**按载具类型各自独立**：
    ///   主战 6 台 → 自己铺满全线；轻坦 6 台 → 也自己铺满全线；重装 6 台 → 集中在城区段。
    ///   （旧版按"同队同域"统编号，结果主战占前半条线、轻坦占后半条线，各自只铺一半。）
    /// 玩家不参与 AI 编号，固定 z 中点。
    /// </summary>
    float SpawnLane(UnitState u, Domain domain, string defId, out int count, out int idx)
    {
        var same = new List<UnitState>();
        foreach (var x in units)
        {
            if (x.team != u.team || x.isHuman) continue;   // 玩家不进编号
            if (DomainOf(x) != domain) continue;
            if (x.prefId != defId) continue;               // 同类型才一起排
            same.Add(x);
        }
        same.Sort((a, b) => a.index.CompareTo(b.index));
        count = same.Count;
        idx = same.IndexOf(u);
        if (count <= 1 || idx < 0) return 0.5f;
        float t = (idx + 0.5f) / count;
        // 2026-11-19：轻坦 / 攻击机错开**半格**。
        // 类型独立编号后，"主战 6 台"和"轻坦 6 台"的序列完全一样，
        // 会落在同一条 z 上（只靠 x 差 16 m 分开，看着还是一条线）。
        if (defId == MatchSettings.IdLight || defId == MatchSettings.IdAttacker)
            t = Mathf.Repeat(t + 0.5f / count, 1f);
        return Mathf.Lerp(0.08f, 0.92f, t);
    }

    /// <summary>
    /// 出生点。
    ///
    /// 2026-11-19 用户："你的坦克和飞机等等的排布也没有拍到山区啊之类的，感觉不够分开"
    /// —— 上一版虽然 z 铺开了，但所有单位都还在 **x = ±640 那一条竖线**上，看着就是一支纵队。
    ///
    /// 现在改成**出生片区**：
    ///   · z：同队同域按序号沿全线均匀铺开（0.06~0.94），间距随人数自适应
    ///   · x：按序号分 4 排朝战场纵深错开（每排 60 m，最多 180 m），**玩家留在最外侧出生线**
    ///   · 目标点周围 80 m 螺旋搜索：这样自然会落到平地、缓坡、山脚等不同地形上
    ///   · 坡度上限从 12° 放宽到 20°（原来卡 12° 基本只能待在平原，山上永远没人）
    ///   · 仍保证 16 m 间距（飞机 60 m）、船 26 m，不会互撞，也不会撞玩家
    /// </summary>
    /// <summary>
    /// 出生线上"这个域 / 这个载具能站"的 z 区间（20 m 采样后合并成连续段）。
    ///
    /// 2026-11-19：原来 z 是按**整条线**均匀铺的，但地图靠海那一段（z > 约 430）整片是深水
    /// （实测 (-200,806) 地形高度 −81 m、水深 85 m），于是后排单位全被判成"找不到点"。
    /// 现在先算出可用段，再把槽位映射到"可用总长"上 —— 从根上不会有人被分到海里。
    /// </summary>
    List<Vector2> UsableSpans(float lineX, float inward, Domain domain, float maxSlope)
    {
        var spans = new List<Vector2>();
        float zMin = battlefield.spawnLineZMin, zMax = battlefield.spawnLineZMax;
        const float step = 20f;
        // 2026-11-19：横向也要采样。只按 x=出生线 单点判断的话，山脚段（出生线正好压在陡壁上
        // 但往里 200 m 就是平地）会被误判成"不可用"，可用段被砍掉一大半，单位全挤在中间。
        float[] offs = { 0f, 110f, 220f, 330f, 440f, 550f };
        float segStart = float.NaN, prev = 0f;
        for (float z = zMin; z <= zMax + 0.001f; z += step)
        {
            bool ok = false;
            foreach (float off in offs)
            {
                float x = lineX + inward * off;
                if (Mathf.Abs(x) > 900f) continue;
                if (!FitsDomain(x, z, domain)) continue;
                if (domain != Domain.Air && BattlefieldTerrain.SampleSteepness(x, z) > maxSlope) continue;
                ok = true; break;
            }
            if (ok)
            {
                if (float.IsNaN(segStart)) segStart = z;
                prev = z;
            }
            else if (!float.IsNaN(segStart))
            {
                spans.Add(new Vector2(segStart, prev));
                segStart = float.NaN;
            }
        }
        if (!float.IsNaN(segStart)) spans.Add(new Vector2(segStart, prev));
        return spans;
    }

    /// <summary>把 0~1 的槽位映射到"可用段总长"上。</summary>
    static float LaneToZ(float t, List<Vector2> spans, float fallback)
    {
        if (spans == null || spans.Count == 0) return fallback;
        float total = 0f;
        foreach (var s in spans) total += s.y - s.x;
        if (total <= 0.01f) return fallback;
        float want = Mathf.Clamp01(t) * total;
        foreach (var s in spans)
        {
            float len = s.y - s.x;
            if (want <= len) return s.x + want;
            want -= len;
        }
        return spans[spans.Count - 1].y;
    }

    Vector3 FindSpawnPoint(UnitState u, VehicleDef def)
    {
        var domain = def.domain;
        bool ally = u.team == 0;
        float baseX = ally ? battlefield.allySpawnLineX : battlefield.enemySpawnLineX;
        float inward = ally ? 1f : -1f;      // 朝地图中心的方向（我方往 +x，敌方往 -x）
        float zMin = battlefield.spawnLineZMin, zMax = battlefield.spawnLineZMax;

        // 2026-11-19：坡度上限**按载具**给，不再一律 20°。
        // 车库卡片上的实际能力：轻坦 35° / 主战 29° / 重装 22°。
        // 出生阈值取"比能力稍低一档"，保证生下来立刻能开（贴上限会原地打滑）。
        bool heavy = def.id == MatchSettings.IdHeavy;
        float maxSlope = def.id == MatchSettings.IdLight ? 33f : heavy ? 20f : 28f;

        int laneCount, laneIdx;
        float lane = SpawnLane(u, domain, def.id, out laneCount, out laneIdx);
        // 槽位映射到"出生线上真正站得住的段"，不再硬按整条线铺
        float zWant = LaneToZ(lane, UsableSpans(baseX, inward, domain, maxSlope), Mathf.Lerp(zMin, zMax, lane));

        // 2026-11-19 用户："重装坦克主要集中城区"。
        // 城区建筑实际铺在 z ≈ ±260（Props/Buildings 统计）。
        // ⚠️ 不能"先按全线算再钳到 ±320" —— 那样钳完有几台会全挤在 ±320 这条边界上。
        // 重装的槽位本来就已经按"同类型"独立编号（SpawnLane），直接把 0~1 映射到城区段即可。
        if (heavy)
            zWant = Mathf.Lerp(Mathf.Clamp(-320f, zMin, zMax), Mathf.Clamp(320f, zMin, zMax), lane);

        // 2026-11-19 用户："他们都主要在出生那条线上，只能微弱改变前后，不能直接贴脸到据点。"
        // —— x 纵深从 5 排 110 m（最多 550 m，会顶到据点）收回成 3 档 × 40 m（最多 80 m）。
        // 玩家贴线（第 0 档）。
        int row = u.isHuman ? 0 : (laneIdx < 0 ? 0 : laneIdx) % 3;
        float xWant = baseX + inward * (row * 40f);

        // ---------- 船：必须整条船都在够深的水里 ----------
        // 2026-11-19 **真 bug**：这里原来直接传 `zWant`，而 `FindSeaSpawn(lineX, lane, ...)` 的
        // 第二个参数是 **0~1 的槽位**（它第一行就是 `Mathf.Lerp(zMin, zMax, lane)`）。
        // 把米当 lane 传进去之后 `Mathf.Lerp` 会把 t 钳到 [0,1]：
        //   zWant > 0 的船全部拿到 z = zMax(967)；zWant < 0 的拿到 z = zMin(-967)（那边是陆地，
        //   第一段循环全失败 → 退到"全图找最远水域"）。实测满编 12 条船全挤在 z=937/967 两排。
        // 现在把 zWant 反算回 0~1 再传，`FindSeaSpawn` 里那次 Lerp 就成了恒等变换，
        // 船以分配到的 zWant 为中心在自己那段水域里找位置。
        if (domain == Domain.Sea)
            return FindSeaSpawn(xWant, Mathf.InverseLerp(zMin, zMax, zWant), zMin, zMax, u.index);

        float minDist = domain == Domain.Air ? 60f : 16f;
        const float searchR = 220f;          // 搜索半径 80 → 220 m，附近没好地形就往远处找

        Vector3 best = new Vector3(xWant, 0f, zWant);
        bool found = false;
        for (float r = 0f; r <= searchR + 0.001f && !found; r += 8f)
        {
            int dirs = r <= 0.001f ? 1 : 8;
            for (int d = 0; d < dirs; d++)
            {
                float ang = d * Mathf.PI * 2f / 8f;
                float x = xWant + Mathf.Cos(ang) * r;
                float z = zWant + Mathf.Sin(ang) * r;
                if (Mathf.Abs(x) > 900f) continue;
                if (z < zMin || z > zMax) continue;
                if (!FitsDomain(x, z, domain)) continue;
                // 别生在陡坡上（2026-11-06：边界加了山脉，出生线原来在 ±933 正好落进山里）；
                // 2026-11-19 放宽到 20°，让单位能落到山脚/缓坡上，不再全挤在平原
                if (domain != Domain.Air && BattlefieldTerrain.SampleSteepness(x, z) > maxSlope) continue;
                if (!SpawnClear(new Vector3(x, 0f, z), minDist)) continue;
                best = new Vector3(x, 0f, z);
                found = true; break;
            }
        }
        if (!found)
        {
            // 2026-11-19 **真 bug**：这里原来是
            //     best = new Vector3(Clamp(xWant), 0, Clamp(zWant));
            // —— **完全不校验地形**。而 z 分配是按全线均匀铺的，靠海那一段（z > 约 430）
            // 整片是深水（实测 (-200,806) 地形高度 -81 m、水深 85 m），220 m 内找不到合法点，
            // 于是轻坦直接被塞进海里。用户看到的就是"坦克位置乱七八糟"。
            //
            // 现在兜底改成**沿 z 逐步扫描**，找最近的一块合法陆地（先往地图中心方向，再往另一侧）。
            float mid = (zMin + zMax) * 0.5f;
            int towardCenter = zWant > mid ? -1 : 1;
            for (float dz = 20f; dz <= (zMax - zMin) && !found; dz += 20f)
            {
                for (int s = 0; s < 2 && !found; s++)
                {
                    float z = zWant + (s == 0 ? towardCenter : -towardCenter) * dz;
                    if (z < zMin || z > zMax) continue;
                    for (float dx = 0f; dx <= 200f && !found; dx += 40f)
                    {
                        for (int k = 0; k < (dx <= 0.001f ? 1 : 2) && !found; k++)
                        {
                            float x = xWant + (k == 0 ? dx : -dx);
                            if (Mathf.Abs(x) > 900f) continue;
                            if (!FitsDomain(x, z, domain)) continue;
                            if (domain != Domain.Air && BattlefieldTerrain.SampleSteepness(x, z) > maxSlope) continue;
                            // 这一层不再要求 16 m 间距（已经没得挑了），但至少别和别人同点
                            if (!SpawnClear(new Vector3(x, 0f, z), 10f)) continue;
                            best = new Vector3(x, 0f, z);
                            found = true;
                        }
                    }
                }
            }
        }
        if (!found)
        {
            // 最后一级兜底：整条出生线 + 400 m 纵深里找任意一块陆地，宁可挤也要在陆上
            for (float z = zMin; z <= zMax && !found; z += 30f)
                for (float dx = 0f; dx <= 400f && !found; dx += 60f)
                {
                    float x = xWant + inward * dx;
                    if (Mathf.Abs(x) > 900f) continue;
                    if (!FitsDomain(x, z, domain)) continue;
                    if (domain != Domain.Air && BattlefieldTerrain.SampleSteepness(x, z) > maxSlope) continue;
                    best = new Vector3(x, 0f, z);
                    found = true;
                }
        }
        if (!found) best = new Vector3(Mathf.Clamp(xWant, -900f, 900f), 0f, Mathf.Clamp(zWant, zMin, zMax));

        float ground = BattlefieldTerrain.SampleHeight(best.x, best.z);
        float y;
        switch (domain)
        {
            case Domain.Air: y = Mathf.Max(ground, BattlefieldTerrain.WaterLevel) + 220f; break;
            default: y = ground + 1.2f; break;
        }
        return new Vector3(best.x, y, best.z);
    }

    /// <summary>
    /// 船的出生点：**整条船**（船体中心 ±10 m 五个采样点）都要 ≥3 m 深。
    /// 出生线整段是浅滩时，退到全图找最深的水域，绝不再往沙滩上放。
    /// </summary>
    Vector3 FindSeaSpawn(float lineX, float lane, float zMin, float zMax, int slot)
    {
        float wy = BattlefieldTerrain.WaterLevel + 0.5f;
        float zWant = Mathf.Lerp(zMin, zMax, lane);

        // 1) 先在自己的槽位附近找（够深 **且** 附近没有别的船）。
        //    2026-11-18 深夜：和陆战一样改成"以分配到的位置为中心小范围微调"，
        //    不再从固定起点向两侧大范围扫 —— 那样几条船会挤在同一段水域。
        for (float off = 0f; off <= 140f; off += 6f)
        {
            int tries = off <= 0.001f ? 1 : 2;
            for (int s = 0; s < tries; s++)
            {
                float z = zWant + (s == 0 ? off : -off);
                if (z < zMin || z > zMax) continue;
                if (!SeaFits(lineX, z)) continue;
                if (!SpawnClear(new Vector3(lineX, 0f, z), 26f)) continue;
                return new Vector3(lineX, wy, z);
            }
        }

        // 2) 出生线不行：全图扫一遍找够深、且离别的船最远的水域。
        //    ⚠️ 旧版这里直接返回"全局最深的那个点"——那是个**固定坐标**，
        //    所有船都会拿到同一个点，于是开局两条船叠在一起（用户报的问题）。
        Vector3 best = new Vector3(lineX, wy, Mathf.Lerp(zMin, zMax, 0.5f));
        float bestScore = -999f;
        const float half = 900f;
        for (float gx = -half; gx <= half; gx += 25f)
            for (float gz = -half; gz <= half; gz += 25f)
            {
                float d = BattlefieldTerrain.WaterDepth(gx, gz);
                // 2026-11-16：以前只要 d>0（哪怕 10 cm 浅滩）就算"能放船"，
                // 海平面抬高后浅滩变多，船就被放到沙滩上。现在至少 2.5 m 深。
                if (d < 2.5f) continue;
                if (!SeaFits(gx, gz)) continue;
                // 得分 = 水深 + 与已有载具的最近距离（距离优先，避免叠船）
                float near = NearestVehicleDist(new Vector3(gx, 0f, gz));
                float score = Mathf.Min(near, 120f) * 10f + d;
                if (score <= bestScore) continue;
                bestScore = score;
                best = new Vector3(gx, wy, gz);
            }

        // 3) 兜底：真的全图都挤，按槽位错开
        if (NearestVehicleDist(best) < 26f)
            best += Vector3.forward * ((slot - 2) * 30f);
        return best;
    }

    /// <summary>离最近的活着的载具有多远（出生点避让用）。</summary>
    static float NearestVehicleDist(Vector3 p)
    {
        float best = 9999f;
        var all = Object.FindObjectsByType<VehicleCombat>(FindObjectsSortMode.None);
        foreach (var v in all)
        {
            if (v == null || !v.Alive) continue;
            float d = Vector3.Distance(new Vector3(v.transform.position.x, 0f, v.transform.position.z),
                                       new Vector3(p.x, 0f, p.z));
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>这个点周围有没有别的载具（出生避让）。</summary>
    static bool SpawnClear(Vector3 p, float minDist) => NearestVehicleDist(p) >= minDist;

    /// <summary>这个点能不能放船：船体中心 + 前后左右各 10 m 都要 ≥3 m 深。</summary>
    static bool SeaFits(float x, float z)
    {
        const float halfLen = 10f;
        if (BattlefieldTerrain.WaterDepth(x, z) < 3.0f) return false;
        if (BattlefieldTerrain.WaterDepth(x + halfLen, z) < 3.0f) return false;
        if (BattlefieldTerrain.WaterDepth(x - halfLen, z) < 3.0f) return false;
        if (BattlefieldTerrain.WaterDepth(x, z + halfLen) < 3.0f) return false;
        if (BattlefieldTerrain.WaterDepth(x, z - halfLen) < 3.0f) return false;
        return true;
    }

    static bool FitsDomain(float x, float z, Domain domain)
    {
        if (domain == Domain.Air) return true;
        float ground = BattlefieldTerrain.SampleHeight(x, z);
        float depth = BattlefieldTerrain.WaterDepth(x, z);
        if (domain == Domain.Sea) return depth >= 2.5f;
        return depth <= 0f && BattlefieldTerrain.SampleSteepness(x, z) < 18f;
    }

    // ---------------- 资源点 ----------------

    void UpdateZones(float dt)
    {
        RefreshCache();
        foreach (var z in zones)
        {
            int team0 = 0, team1 = 0;
            foreach (var kv in _cache)
            {
                var v = kv.Key;
                if (v == null || !v.Alive || !z.CanBeCapturedBy(v)) continue;
                if (!z.Contains(v.transform.position)) continue;
                if (v.Team == 0) team0++; else if (v.Team == 1) team1++;
            }

            int capping = (team0 > 0 && team1 == 0) ? 0 : (team1 > 0 && team0 == 0) ? 1 : -1;

            if (capping < 0)
            {
                z.progress = Mathf.MoveTowards(z.progress, 0f, dt / Mathf.Max(0.01f, captureSeconds));
                if (z.progress <= 0f) z.progressTeam = -1;
                continue;
            }

            if (z.owner == capping) { z.progress = 1f; z.progressTeam = capping; continue; }
            if (z.progressTeam != capping) { z.progressTeam = capping; z.progress = 0f; }

            z.progress += dt / Mathf.Max(0.01f, captureSeconds);
            if (z.progress >= 1f)
            {
                z.progress = 1f;
                int old = z.owner;
                z.owner = capping;
                BattleEvents.RaiseZoneCaptured(z);
                if (old != capping)
                {
                    PushFeed($"{z.DisplayName} 被{(capping == 0 ? "我方" : "敌方")}占领");
                    // 首次占领一次性 VP（从中立或敌方手里抢下来都给）
                    if (vpCaptureBonus > 0) AddVp(capping, vpCaptureBonus);
                }
            }
        }
    }

    /// <summary>
    /// 胜利点累积：每个我方据点每 vpPerZoneSeconds 秒 +1 VP。
    /// 用"计时器 + 取整"而不是"每帧加浮点"，避免帧率不同导致积分速率漂移。
    /// </summary>
    void UpdateVp(float dt)
    {
        if (matchOver) return;

        // 2026-11-17：玩家可在「游戏调整」里调积分上涨倍率（0.5~3.0）。
        // 倍率越高 → 每个周期越短 → VP 涨得越快。
        // 用 `1 / mul` 而不是直接乘 VP 增量，是为了保持"整数周期结算"的精度
        // （直接乘增量会出现 0.7 VP 这种小数，HUD 上会跳得很难看）。
        float mul = Mathf.Clamp(MatchSettings.VpRateMul, MatchSettings.VpRateMin, MatchSettings.VpRateMax);
        float period = vpPerZoneSeconds / Mathf.Max(0.01f, mul);

        _vpTimer += dt;
        if (_vpTimer < period) return;
        // 一次可能跨过多个周期（低帧率 / 卡顿），按整数个周期结算
        int cycles = Mathf.FloorToInt(_vpTimer / period);
        _vpTimer -= cycles * period;

        int owned0 = 0, owned1 = 0;
        foreach (var z in zones)
        {
            if (z.owner == 0) owned0++;
            else if (z.owner == 1) owned1++;
        }
        if (owned0 > 0) AddVp(0, owned0 * cycles);
        if (owned1 > 0) AddVp(1, owned1 * cycles);
    }

    /// <summary>
    /// 资源点产金是**全队加成**：占着的点每 tick 给该队每个单位各发一份。
    ///
    /// 2026-11-17 经济系统：
    ///   · 没车也照常领（否则死了永远复活不了）
    ///   · 加上 **生存补贴**（`match.survivalIncome`，只给活着的单位）—— 奖励"在场"
    ///   · 加上 **反雪球系数**：落后方 ×1.3、领先方 ×0.85（按 VP 判定）
    /// </summary>
    void UpdateIncome(float dt)
    {
        _incomeTimer += dt;
        if (_incomeTimer < match.captureInterval) return;
        _incomeTimer -= match.captureInterval;

        int income0 = 0, income1 = 0;
        foreach (var z in zones)
        {
            if (z.owner < 0 || z.def == null) continue;
            if (z.owner == 0) income0 += z.def.incomePerTick;
            else income1 += z.def.incomePerTick;
        }

        // 反雪球（2026-11-17）：把"一边倒的垃圾时间"压缩，但不会让领先方白打
        income0 = Mathf.RoundToInt(income0 * CatchUpMul(0));
        income1 = Mathf.RoundToInt(income1 * CatchUpMul(1));

        // 生存补贴：只给**场上还有车**的单位（挂机不出车不给钱）
        int surv = match != null ? match.survivalIncome : 0;

        foreach (var u in units)
        {
            int baseInc = u.team == 0 ? income0 : income1;
            int gain = baseInc + (u.HasVehicle ? surv : 0);
            if (gain > 0) u.gold += gain;
        }
    }

    /// <summary>
    /// 反雪球系数：落后方 ×1.3、领先方 ×0.85、均势 ×1.0。
    /// 判定用 VP 比值（不看得分绝对值，避免"刚开局双方都是 0"的除零）。
    /// </summary>
    float CatchUpMul(int team)
    {
        int me = vp[Mathf.Clamp(team, 0, 1)];
        int foe = vp[1 - Mathf.Clamp(team, 0, 1)];
        if (me <= 0 && foe <= 0) return 1f;              // 都还没分
        float ratio = foe > 0 ? (float)me / foe : 2f;
        if (ratio < 0.6f) return 1.3f;                    // 明显落后：多拿 30%
        if (ratio > 1.5f) return 0.85f;                   // 明显领先：少拿 15%
        return 1f;
    }

    // ---------------- 击杀 / 胜负 ----------------

    void OnVehicleDestroyed(VehicleCombat victim, VehicleCombat killer)
    {
        if (victim == null) return;

        if (_map.TryGetValue(victim, out var victimUnit)) _map.Remove(victim);
        else victimUnit = null;

        if (victimUnit != null)
        {
            victimUnit.vehicle = null;
            // 2026-11-17：死了记一次（复活递增用）。首次出击不算，这里只统计真正的阵亡。
            victimUnit.respawnCount++;

            if (victimUnit.isHuman)
            {
                PushFeed("你的载具被击毁");
                _garageAt = Time.unscaledTime + garageDelay;
            }
            else if (true)   // 命数已删除：AI 死了总能复活（钱够就行）
            {
                victimUnit.respawnAt = Time.time + aiRespawnDelay;
            }
        }

        // 击杀奖励归**击杀者个人**
        if (killer != null && _map.TryGetValue(killer, out var killerUnit) && killerUnit != victimUnit)
        {
            int reward = Mathf.RoundToInt((victim.Def != null ? victim.Def.price : 100) * match.killRewardRate);
            killerUnit.gold += reward;
            kills[Mathf.Clamp(killerUnit.team, 0, 1)]++;
            if (vpPerKill > 0) AddVp(killerUnit.team, vpPerKill);
            PushFeed($"{killerUnit.Label} 击毁 {victim.name}  +{reward}");
            // 2026-11-16 用户要求：玩家亲手击毁时来一发击杀特效（不是死亡爆炸，是击杀提示）
            if (killerUnit.isHuman)
                MatchHUD.ShowKillFx(victim.Def != null ? victim.Def.displayName : victim.name);
        }
        else
        {
            PushFeed($"{victim.name} 被摧毁");
        }

        CheckTeamWipe();
    }

    void CheckTeamWipe()
    {
        if (matchOver) return;
        // 2026-11-17 用户要求：**删掉 3 条命判负**。
        // 原来这里会先判"命用光就输"，抢在 VP 之前生效 ——
        // 你 VP 800:300 大幅领先，只要命用光照样判输，与"VP 决定胜负"直接冲突。
        // 现在胜负只看 VP + 15 分钟时限；复活只受金币约束（钱不够最便宜那台会自动补）。
        // 保留"全员出局"这一条：它读 TeamAlive（场上还有没有车），不依赖命数。
        if (TeamAlive(1) == 0 && TeamAlive(0) > 0) { EndMatch(0, "敌方全部出局"); return; }
        if (TeamAlive(0) == 0 && TeamAlive(1) > 0) { EndMatch(1, "我方全部出局"); return; }
    }

    /// <summary>
    /// 时间到 / 触发收尾时的判定顺序（2026-11-17）：
    ///   1. VP 高者胜（**唯一主判据**）
    ///   2. VP 相同 → 判我方胜（原来的兜底，不让玩家"白打一局还输"）
    /// 命数已删除，不再参与判定。
    /// </summary>
    void EndByLives(string reason)
    {
        if (vp[0] != vp[1])
        {
            EndMatch(vp[0] > vp[1] ? 0 : 1, $"{reason} · 比胜利点 {vp[0]}:{vp[1]}");
            return;
        }
        EndMatch(0, reason + " · 平局判我方胜");
    }

    public void EndMatch(int winner, string reason)
    {
        if (matchOver) return;
        matchOver = true;
        winnerTeam = winner;
        resultText = $"{reason} · {(winner == 0 ? "我方胜利" : "我方失败")}";
        PushFeed(resultText);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void Restart()
    {
        matchOver = false;
        BattleEvents.VehicleDestroyed -= OnVehicleDestroyed;
        // 2026-11-12：从暂停面板重开时 timeScale 还是 0、Paused 还是 true，
        // LoadScene 不会清静态字段 —— 不在这里复位，新一局会一进去就冻住。
        Time.timeScale = 1f;
        AudioListener.pause = false;
        PauseScreen.ClearPaused();
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // ---------------- 工具 ----------------

    public void PushFeed(string text)
    {
        feed.Add(new FeedEntry { text = text, time = Time.time });
        while (feed.Count > 6) feed.RemoveAt(0);
    }

    public string TimeText
    {
        get
        {
            int t = Mathf.Max(0, Mathf.CeilToInt(timeLeft));
            return $"{t / 60:00}:{t % 60:00}";
        }
    }
}
