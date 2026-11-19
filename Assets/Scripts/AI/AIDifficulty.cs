using UnityEngine;

/// <summary>
/// AI 难度档案：把「同一个 AI 在不同难度下做什么」集中成一张表。
///
/// ============ 2026-11-17 新增的背景 ============
///
/// 之前 AI 有金币但**一分钱都不花**（除了复活买载具）：
///   · 受伤只会后撤（`Think()` 里 lowHp → 跑 150 m），**永远不会回血**
///   · 弹药打光（飞机导弹 / 炮艇鱼雷）**永远不会补**
///   · 从不主动换车（`prefId` 开局定死）
/// 而玩家能按 TAB 买维修包 / 模块修复 / 弹药补给 —— 于是玩家可以「修好再打」，
/// AI 只能硬吃，越打越亏。
///
/// 这个类解决两件事：
///   1. **让 AI 会用商店那三样东西**（维修包 / 模块修复 / 弹药补给）
///   2. **按难度分层**：轻松档基本不花钱、普通档会修但犹豫、原版档精打细算还会攒钱换车
///
/// 参数从 `MatchSettings.AiSkill`（0.55 / 0.75 / 1.0 三档）映射过来，
/// 和 `VehicleAI` 里已有的视野 / 瞄准误差 / 干扰弹反应率用同一套 Skill01 口径，保证一致。
///
/// ⚠️ 为什么不用 `AIDef` 那个 ScriptableObject：
/// 它和 `Assets/Data/AI_Default.asset` 都是**死代码**（全工程零引用），
/// 而且资产文件是空的。留在那儿容易让人以为「改资产就能调 AI」——
/// 实际 AI 参数全在 `VehicleAI.cs` 的字段里。这里直接在代码里定义档案，改起来更直接。
/// </summary>
public static class AIDifficulty
{
    /// <summary>
    /// 一档难度的完整行为描述。
    /// </summary>
    public struct Profile
    {
        // ---- 经济行为 ----
        /// <summary>血量低于该比例才考虑买维修包（0 = 永不买）。</summary>
        public float repairBelowHp;
        /// <summary>钱包至少留多少才肯买维修包（避免花光买不起复活）。</summary>
        public int repairKeepGold;
        /// <summary>关键模块坏了（炮管/弹药架/发动机/履带）是否买模块修复包。</summary>
        public bool buysModuleRepair;
        /// <summary>模块修复包的钱包门槛。</summary>
        public int moduleRepairKeepGold;
        /// <summary>主力弹药剩多少比例以下才买补给（1 = 一缺就买）。</summary>
        public float ammoRefillBelow;
        /// <summary>弹药补给的钱包门槛。</summary>
        public int ammoKeepGold;
        /// <summary>两次采购之间的最短间隔（秒），避免每帧狂买。</summary>
        public float buyCooldown;
        /// <summary>买完维修后是否"回身再战"（true = 不再因为 lowHp 后撤）。</summary>
        public bool reengageAfterRepair;

        // ---- 换车 ----
        /// <summary>是否会在复活时挑更贵的车（true = 用攒下的钱升级）。</summary>
        public bool upgradesVehicle;
        /// <summary>钱包达到 车价 × 该倍数 才升级（避免刚够就花光）。</summary>
        public float upgradeGoldMul;

        // ---- 后撤阈值（低难度跑得更早、更远） ----
        /// <summary>低于该血量比例就后撤。</summary>
        public float retreatHp;
        /// <summary>后撤时拉开多远（米）。</summary>
        public float retreatDistance;
    }

    /// <summary>
    /// 按 Skill（0.3~1.0）取档案。
    /// 三档实测值：
    ///   轻松 0.55 → 几乎不花钱（只有残血到 25% 才买一次维修，且要留 120 金）
    ///   普通 0.75 → 会修、会补弹，但不换车
    ///   原版 1.00 → 精打细算，修 + 补 + 攒钱升级载具
    /// </summary>
    public static Profile Of(float skill)
    {
        float s01 = Mathf.Clamp01((skill - 0.4f) / 0.6f);   // 与 VehicleAI.Skill01 同口径

        // 插值出三档之间的连续过渡（不写死 if/else，调 Skill 值时行为平滑变化）
        var p = new Profile();

        // ---- 维修包 ----
        // 轻松：残血到 25% 才买，而且要留 120 金（基本等于不怎么修）
        // 普通：40% 就买，留 100 金
        // 原版：55% 就买，留 80 金（该花就花，因为活得久更赚）
        p.repairBelowHp = Mathf.Lerp(0.25f, 0.55f, s01);
        p.repairKeepGold = Mathf.RoundToInt(Mathf.Lerp(120f, 80f, s01));

        // ---- 模块修复 ----
        // 轻松（Skill 0.55，s01=0.25）：不买，炮管坏了就硬打
        // 普通（0.75，s01≈0.58）及以上：买
        p.buysModuleRepair = s01 >= 0.45f;
        p.moduleRepairKeepGold = Mathf.RoundToInt(Mathf.Lerp(140f, 90f, s01));

        // ---- 弹药补给 ----
        // 轻松：要打到只剩 15% 才补，留 100 金
        // 原版：剩 45% 就补，留 70 金
        p.ammoRefillBelow = Mathf.Lerp(0.15f, 0.45f, s01);
        p.ammoKeepGold = Mathf.RoundToInt(Mathf.Lerp(100f, 70f, s01));

        // ---- 采购节奏 ----
        // 轻松：8 秒才看一次（反应慢）；原版：2 秒
        p.buyCooldown = Mathf.Lerp(8f, 2f, s01);
        p.reengageAfterRepair = s01 >= 0.5f;

        // ---- 换车 ----
        // 只有原版档会攒钱升级（s01 >= 0.85 ≈ Skill 0.91 以上）
        p.upgradesVehicle = s01 >= 0.85f;
        p.upgradeGoldMul = Mathf.Lerp(2.2f, 1.6f, s01);

        // ---- 后撤 ----
        // 轻松：28% 就跑、跑 150 m（跑得早、跑得远 = 更容易活，但也更少输出）
        // 原版：18% 才退、只退 90 m（更能扛、更快回到战斗）
        p.retreatHp = Mathf.Lerp(0.28f, 0.18f, s01);
        p.retreatDistance = Mathf.Lerp(150f, 90f, s01);

        return p;
    }

    /// <summary>当前对局难度对应的档案。</summary>
    public static Profile Current => Of(MatchSettings.AiSkill);

    /// <summary>给 UI / 日志用的一句话描述。</summary>
    public static string Describe(float skill)
    {
        var p = Of(skill);
        string repair = p.repairBelowHp > 0.02f
            ? $"残血 {p.repairBelowHp:P0} 买维修（留 {p.repairKeepGold} 金）"
            : "不修";
        string mod = p.buysModuleRepair ? "会修模块" : "不修模块";
        string ammo = $"弹药低于 {p.ammoRefillBelow:P0} 补给";
        string up = p.upgradesVehicle ? $"攒到车价×{p.upgradeGoldMul:F1} 换更贵的车" : "不换车";
        return $"{repair} · {mod} · {ammo} · {up} · 后撤 {p.retreatHp:P0}";
    }
}
