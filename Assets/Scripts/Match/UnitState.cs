using UnityEngine;

/// <summary>
/// 一个参战单位（一个人）。5v5 = 双方各 5 个 UnitState。
///
/// 经济是**个人**的：金币归自己，买车花自己的钱，击杀奖励进自己口袋。
/// 唯一共享的是资源点产金 —— 占下来的点每 10 秒给**全队每人**发一份。
/// **命数机制已于 2026-11-17 删除**：复活只受金币约束，胜负只看 VP。
/// `lives` 字段保留只为兼容旧存档/旧代码，恒为 999，不再参与任何判定。
/// </summary>
public class UnitState
{
    public int team;
    public int index;
    public bool isHuman;

    public int gold;
    /// <summary>已废弃（2026-11-17 删命数）。恒为 999，不要再读它做判定。</summary>
    public int lives;

    public VehicleCombat vehicle;
    public VehicleDef lastDef;
    public float respawnAt = -1f;

    /// <summary>死过几次（复活递增用）。首次出击不算。</summary>
    public int respawnCount;
    /// <summary>主动换车几次（换车递增用）。</summary>
    public int swapCount;

    /// <summary>
    /// 当前这台车**实际花了多少钱**（含复活/换车倍率）。
    /// 2026-11-18 用户要求："旧车当时的购买价格的一半才是要标的价" ——
    /// 所以回收时必须按这个算，不能用 VehicleDef.price 原价。
    /// 例：买船第一次 320、第二次 353，第二次的回收就是 176，不是 160。
    /// </summary>
    public int paidForCurrent;

    /// <summary>
    /// 2026-11-11：开局生成用。海/空载具要等解锁，这类单位先挂"还没第一次出场"，
    /// 到时间由 UpdateRespawns 补上，而且**不扣命**（第一次出场不算复活）。
    /// </summary>
    public bool firstSpawnPending;

    /// <summary>这一号位默认开什么车（MatchSettings 里按类型配的数量决定，空 = 按域随机）。</summary>
    public string prefId;

    public bool HasVehicle => vehicle != null && vehicle.Alive;

    /// <summary>
    /// 场上没车。
    ///
    /// ⚠️ 2026-11-17 傍晚修正语义：原来它叫 `Eliminated` 且被用在**收入过滤**上
    /// （`if (!u.Eliminated) u.gold += income`），于是"没车就领不到占点收入" ——
    /// 配合当时的"免费送最便宜的车 + 复活补钱"，就变成死了白送车、
    /// 钱也不用自己攒，灵魂出窍完全没有存在意义。
    /// 现在改名 `NoVehicle`，**只表示"当前没车"**，不再参与任何收入/胜负判定。
    /// </summary>
    public bool NoVehicle => !HasVehicle;

    public string Label => isHuman ? "你" : $"{(team == 0 ? "我方" : "敌方")}{index + 1}号";
}
