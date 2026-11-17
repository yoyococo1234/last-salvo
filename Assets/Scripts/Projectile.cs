using UnityEngine;

/// <summary>
/// 炮弹：抛物线弹道（受重力），逐帧扫掠检测防穿模，命中载具按三层公式结算伤害。
///
/// 为什么不做直线：2 km 地图地形有起伏，直线弹道贴着地面飞 200 m 就会被土包挡住；
/// 抛物线能越过去，也更像坦克炮。
/// </summary>
public class Projectile : MonoBehaviour
{
    public const float Gravity = 9.8f;
    public static bool DebugHits = false;      // 排查用：打印这一发命中了什么

    Vector3 _vel;
    float _damage;
    float _life;
    float _maxRange;      // 射程上限（0 = 不限）
    float _travelled;
    AmmoDef _ammo;
    VehicleCombat _owner;

    // ---- 制导（2026-11-07 加，同日删；2026-11-16 23:20 恢复；2026-11-17 11:20 再次关闭）----
    // 用户原话（2026-11-17）："这个子弹飞的有点不像真的子弹了…原先我们那种打的子弹是怎么设计的？"
    //
    // 昨晚 23:20 加的"制导 + 6 m 近炸"让炮弹以 360°/s 拐向目标、飞到 6 m 内自动判定命中，
    // 观感上就是"会追人的魔法弹"，完全不是坦克炮该有的样子。
    //
    // 现在恢复**纯物理弹道**：打出去就按初速 + 重力飞，打不中就是打不中。
    // 锁定只负责"帮你把炮口算准"（LockOnSystem.AimPointFor 会做提前量 + 弹道解算），
    // 不再让炮弹自己拐弯。
    //
    // 保留字段和 SetHoming 接口（默认关闭），以后想做成"导弹"类武器时可以直接复用。
    VehicleCombat _homing;
    float _homingTurn = 360f;      // 度/秒，够快就能拐过来
    public void SetHoming(VehicleCombat target) => _homing = target;

    /// <summary>
    /// 制导弹丸总开关。**默认关闭**：炮弹按纯物理飞，不拐弯、不近炸。
    /// 只有导弹/鱼雷这类真正的制导武器才应该打开（见 VehicleCombat 里的调用处）。
    /// </summary>
    public static bool EnableHoming = false;

    /// <summary>近炸半径（米）：制导弹丸飞到这个距离内直接判定命中。</summary>
    const float ProximityRadius = 6f;

    public void Launch(Vector3 dir, float speed, float damage, AmmoDef ammo, VehicleCombat owner, float maxRange = 0f)
    {
        _vel = dir.normalized * Mathf.Max(1f, speed);
        _damage = damage;
        _ammo = ammo;
        _owner = owner;
        _maxRange = Mathf.Max(0f, maxRange);
        // 生命 = 飞满射程所需时间 + 余量；没给射程就按 12 秒兜底
        _life = _maxRange > 1f ? _maxRange / Mathf.Max(1f, speed) + 0.5f : 12f;
        _travelled = 0f;
        transform.rotation = Quaternion.LookRotation(_vel);

        var tr = gameObject.AddComponent<TrailRenderer>();
        tr.time = 0.35f;
        tr.startWidth = 0.9f;
        tr.endWidth = 0.06f;
        tr.material = new Material(Shader.Find("Sprites/Default"));
        tr.startColor = new Color(1f, 0.85f, 0.4f, 0.9f);
        tr.endColor = new Color(1f, 0.4f, 0.1f, 0f);
    }

    void Update()
    {
        _life -= Time.deltaTime;
        if (_life <= 0f) { Destroy(gameObject); return; }

        _vel += Vector3.down * Gravity * Time.deltaTime;

        // 锁定制导：把速度方向慢慢拐向目标（速度大小不变）。
        // 2026-11-17：**默认关闭** —— 炮弹按纯物理飞，不追人（见 EnableHoming 的注释）。
        if (EnableHoming && _homing != null && _homing.Alive)
        {
            // 瞄准目标**真实中心**（所有渲染器包围盒合并，和锁定框同一口径）
            Vector3 aim = HomingAimPoint(_homing);
            Vector3 want = (aim - transform.position);
            float dist = want.magnitude;
            if (want.sqrMagnitude > 0.01f)
            {
                float spd = _vel.magnitude;
                _vel = Vector3.RotateTowards(_vel, want.normalized * spd, _homingTurn * Mathf.Deg2Rad * Time.deltaTime, 0f);
                _vel = _vel.normalized * spd;
            }

            // 近炸：飞到目标附近直接判定命中（不再苛求射线正好穿过碰撞盒）。
            // 这是用户要的"飞过去的时候偷偷打击"—— 炮弹本身还是按物理飞，只是判定放宽。
            if (dist <= ProximityRadius)
            {
                HitDirect(_homing, transform.position);
                return;
            }
        }

        Vector3 stepVec = _vel * Time.deltaTime;
        float step = stepVec.magnitude;
        if (step < 0.0001f) return;

        var hits = Physics.RaycastAll(transform.position, stepVec / step, step + 0.3f, ~0, QueryTriggerInteraction.Ignore);
        if (hits != null && hits.Length > 0)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var target = h.collider.GetComponentInParent<VehicleCombat>();
                if (target == _owner) continue;                 // 不打自己
                if (DebugHits)
                    Debug.Log($"[Shell] {name} 命中 {h.collider.name} 距离 {h.distance:F1} m，目标={(target != null ? target.name : "无")}");
                if (target != null && target.Alive)
                {
                    var res = target.TakeHit(_damage, _ammo, transform.position, _owner, h.point, h.normal);
                    VehicleCombat.ShotsHit++;
                    if (_owner != null && _owner.IsPlayer) MatchHUD.ShowHitFeedback(res);
                }
                else
                {
                    // 2026-11-11：打在建筑/地面/岩石上 —— 冒一团烟尘，别只是"啪一下消失"。
                    // 建筑打不穿：炮弹一撞到网格就销毁，不会穿过去打到后面的东西。
                    bool isMg = _ammo != null && !string.IsNullOrEmpty(_ammo.id) && _ammo.id.Contains("mg");
                    ExplosionFx.Impact(h.point, h.normal, isMg ? 1.3f : 3.4f);
                }
                Destroy(gameObject);
                return;
            }
        }

        transform.position += stepVec;
        _travelled += step;
        transform.rotation = Quaternion.LookRotation(_vel);

        // 飞出射程就消失（否则炮弹会一直飞过整张图）
        if (_maxRange > 1f && _travelled >= _maxRange) { Destroy(gameObject); return; }

        if (DebugHits && Time.frameCount % 12 == 0)
            Debug.Log($"[Shell] {name} pos={transform.position:F1} 距地={transform.position.y - BattlefieldTerrain.SampleHeight(transform.position.x, transform.position.z):F1}");
    }

    /// <summary>
    /// 制导目标点 = 目标**真实视觉中心**（所有启用渲染器包围盒合并，排除粒子/拖尾/机枪挂件）。
    /// 和 LockOnSystem.VisualCenter 同一口径，保证"框画在哪、炮弹就拐向哪"。
    /// </summary>
    static Vector3 HomingAimPoint(VehicleCombat v)
    {
        if (v == null) return Vector3.zero;
        bool any = false;
        Bounds b = default;
        foreach (var r in v.GetComponentsInChildren<Renderer>())
        {
            if (r == null || !r.enabled) continue;
            if (r is ParticleSystemRenderer) continue;
            if (r is TrailRenderer) continue;
            if (r is LineRenderer) continue;
            if (!string.IsNullOrEmpty(r.name) && r.name.StartsWith("MG_", System.StringComparison.Ordinal))
                continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any ? b.center : v.transform.position + Vector3.up * 1.2f;
    }

    /// <summary>近炸命中：直接对目标结算伤害（不再依赖射线穿过碰撞盒）。</summary>
    void HitDirect(VehicleCombat target, Vector3 at)
    {
        if (target == null || !target.Alive) return;
        Vector3 dir = (at - target.transform.position);
        if (dir.sqrMagnitude < 0.01f) dir = Vector3.up;
        // 用一个从"炮弹来向"打过去的点，让装甲/入射角判定合理
        var res = target.TakeHit(_damage, _ammo, at - dir.normalized * 5f, _owner, at, dir.normalized);
        VehicleCombat.ShotsHit++;
        if (_owner != null && _owner.IsPlayer) MatchHUD.ShowHitFeedback(res);
        Destroy(gameObject);
    }
}
