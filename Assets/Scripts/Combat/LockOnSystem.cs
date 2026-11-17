using UnityEngine;

/// <summary>
/// Ctrl 锁定瞄准（2026-11-07 用户要求）。
///
/// 按住 Ctrl：自动吸附到"屏幕里能打到、最近的那台敌车"
///   · 炮塔/机头自动对准目标（PlayerTankController 会直接拿锁定点当瞄准点）
///   · HUD 画出锁定框 + 距离 + 是否进入射程（射程内绿色，超射程橙色）
///   · 这时候按左键，炮弹带轻微制导，保证打得中
///
/// 松开 Ctrl 立刻解除。
/// </summary>
public class LockOnSystem : MonoBehaviour
{
    public static bool Active { get; private set; }
    public static VehicleCombat Target { get; private set; }
    public static bool InRange { get; private set; }
    public static float Distance { get; private set; }

    /// <summary>锁定搜索半径（米）。</summary>
    public float maxRange = 1600f;

    /// <summary>2026-11-12 用户要求：**锁定距离上限 500 m**，更远只能自己瞄（机枪/舰炮都一样）。</summary>
    public const float LockRangeCap = 500f;

    /// <summary>调试用：设成某个敌人就强制锁定（不影响正常 Ctrl 逻辑）。</summary>
    public static VehicleCombat DebugTarget;

    static LockOnSystem _inst;
    float _lockTime;
    float _flash;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("LockOnSystem");
        _inst = go.AddComponent<LockOnSystem>();
        Object.DontDestroyOnLoad(go);
    }

    static void Clear()
    {
        Active = false;
        Target = null;
        InRange = false;
    }

    void Update()
    {
        var mm = MatchManager.Instance;
        var player = mm != null ? mm.Player : null;

        // 调试用：DebugTarget 不为空就强制锁定（编辑器里验证瞄准链路用）
        if (DebugTarget != null && DebugTarget.Alive)
        {
            Active = true;
            Target = DebugTarget;
            var pc = mm != null ? mm.Player : null;
            if (pc != null)
            {
                Distance = Vector3.Distance(pc.transform.position, Target.transform.position);
                InRange = Distance <= (pc.Weapon != null ? pc.Weapon.range : 800f);
            }
            _lockTime += Time.deltaTime;
            _flash = Mathf.Max(0f, _flash - Time.deltaTime * 3f);
            return;
        }

        bool want = Bindings.Held(Bindings.Act.LockOn);
        if (!want || player == null || !player.Alive || PauseScreen.Paused || ShopScreen.Open)
        {
            Clear();
            return;
        }

        Active = true;

        // 目标没了 / 死了 / 跑太远 → 重新挑一个
        if (Target == null || !Target.Alive ||
            Vector3.Distance(player.transform.position, Target.transform.position) > maxRange)
        {
            var next = PickTarget(player);
            if (next != Target) { Target = next; _lockTime = 0f; _flash = 1f; }
        }

        if (Target != null)
        {
            _lockTime += Time.deltaTime;
            Distance = Vector3.Distance(player.transform.position, Target.transform.position);
            // 机枪模式下按机枪射程算（不然准星移过去了却打不到）
            float range = player.Weapon != null ? player.Weapon.range : 800f;
            var ptcU = player.GetComponent<PlayerTankController>();
            if (ptcU != null && ptcU.MgMode)
            {
                var dwU = player.DeckWeapon;
                if (dwU != null) range = dwU.range;
            }
            InRange = Distance <= range;
        }
        else
        {
            Distance = 0f;
            InRange = false;
        }

        _flash = Mathf.Max(0f, _flash - Time.deltaTime * 3f);
    }

    /// <summary>挑"屏幕里、有视线、最近"的敌人。</summary>
    VehicleCombat PickTarget(VehicleCombat player)
    {
        // 先在准星附近 22° 找；找不到放宽到 45°（不然稍微偏一点就锁不上）
        // 优先锁谁：飞机（自己在开飞机 / 正在用机枪）优先锁敌机，其余优先锁地面/海面
        bool preferAir = false;
        if (player.Def != null && player.Def.domain == Domain.Air) preferAir = true;
        else
        {
            var ptc = player.GetComponent<PlayerTankController>();
            if (ptc != null && ptc.MgMode) preferAir = true;
        }

        // 2026-11-16 23:05 用户："为什么舰炮还去锁飞机，这不是闹吗"。
        // 舰炮/主炮对空倍率极低（Ammo_heat vsAir 0.1），锁上飞机纯属误导。
        // 规则：**非机枪、非飞机**的载具（陆战/海战主武器）只锁地面/海面目标，
        // 空中目标直接排除（不是加权排后）。只有两种情况例外：
        //   ① 自己在开飞机（空中对空中）
        //   ② 按 V 进了机枪模式（12.7mm 对空 60 DPS，专打飞机）
        bool groundOnly = !preferAir;
        var t = PickTargetInCone(player, 22f, preferAir, groundOnly);
        if (t == null) t = PickTargetInCone(player, 45f, preferAir, groundOnly);
        return t;
    }

    VehicleCombat PickTargetInCone(VehicleCombat player, float coneDeg, bool preferAir, bool groundOnly)
    {
        var cam = Camera.main;
        if (cam == null) return null;

        VehicleCombat best = null;
        float bestScore = float.MaxValue;
        Vector3 from = player.transform.position + Vector3.up * 2f;

        foreach (var v in VehicleCombat.AllAlive())
        {
            if (v == null || !v.Alive || v == player) continue;
            if (v.team == player.team) continue;

            Vector3 aim = v.transform.position + Vector3.up * 1.2f;
            float d = Vector3.Distance(from, aim);

            // 锁定距离：
            //   · 2026-11-12 用户要求：**只有 500 m 以内才能锁定**，更远请自己瞄。
            //     （原来机枪 850 m、舰炮 1200 m 都能锁，玩家反馈"太远了，等于自动瞄准"。）
            float wRange = player.Weapon != null ? player.Weapon.range : 800f;
            var ptcR = player.GetComponent<PlayerTankController>();
            if (ptcR != null && ptcR.MgMode)
            {
                var dwR = player.DeckWeapon;
                if (dwR != null) wRange = dwR.range;
            }
            float lockRange = Mathf.Min(LockRangeCap, wRange);
            if (d > Mathf.Min(maxRange, lockRange)) continue;

            // 必须在屏幕中间区域（避免锁到视野外的东西）
            Vector3 sp = cam.WorldToScreenPoint(aim);
            if (sp.z <= 0f) continue;
            float sx = Mathf.Abs(sp.x / Screen.width - 0.5f);
            float sy = Mathf.Abs(sp.y / Screen.height - 0.5f);
            if (sx > 0.45f || sy > 0.45f) continue;

            // 有视线（被山挡住的不锁）
            // 注意：起点要先从自己车体里推出去，否则第一根射线打到自己 → 永远锁不上
            Vector3 dirL = (aim - from);
            float distL = dirL.magnitude;
            if (distL < 1f) continue;
            dirL /= distL;
            Vector3 fromL = from + dirL * 4f;
            if (Physics.Raycast(fromL, dirL, out var hit, distL, ~0, QueryTriggerInteraction.Ignore))
            {
                var hitVc = hit.collider.GetComponentInParent<VehicleCombat>();
                if (hitVc != v) continue;
            }

            // 用户要求：**优先锁准星方向能打到的最近的** —— 夹角为主、距离为辅
            float ang = Vector3.Angle(cam.transform.forward, aim - cam.transform.position);
            if (ang > coneDeg) continue;                    // 只在准星附近这个夹角内找
            // 偏好类型加权：不是偏好类型的目标分数放大，排到后面去
            bool isAirTarget = v.Def != null && v.Def.domain == Domain.Air;

            // 陆/海主武器不锁空中目标（见 PickTarget 的注释）——彻底排除，不是加权。
            if (groundOnly && isAirTarget) continue;

            float domainMul = (isAirTarget == preferAir) ? 1f : 3.5f;
            float score = (ang * 12f + d * 0.02f) * domainMul;
            if (score < bestScore) { bestScore = score; best = v; }
        }
        return best;
    }

    /// <summary>
    /// 锁定时给玩家用的瞄准点：目标位置 + 提前量（按自己武器的弹速估算飞行时间）。
    /// 这样炮弹是**按物理规则**飞过去的，只是瞄得准（所见即攻击），不是制导。
    ///
    /// ⚠️ 2026-11-16 23:05：锚点必须和锁定框**完全一致**，都用 VisualCenter。
    /// 原来这里用 `transform.position + up*1.2`（炮艇 y=5.70），
    /// 而锁定框用 VisualCenter（y=6.75）—— 框套在船身、炮弹却飞向船底上方 1.05 m 处，
    /// 用户看到的正是"瞄船身、打空气"。
    /// </summary>
    public static Vector3 AimPointFor(VehicleCombat player)
    {
        if (Target == null) return Vector3.zero;
        Vector3 aim = AimAnchor(Target);
        if (player == null) return aim;

        var tvp = Target.GetComponent<VehiclePhysics>();
        Vector3 vel = tvp != null ? tvp.Velocity : Vector3.zero;

        // 2026-11-12 修：以前**永远**用炮塔武器（`player.Weapon`）算提前量。
        // 炮艇按 V 进机枪模式后，炮塔武器可能是鱼雷（弹速只有几十 m/s），
        // 于是按鱼雷算出来的提前量巨大 —— 打 374 m 的飞机要提前几百米，准星自然"怎么都对不上"。
        // 现在按"当前真正开火的那件武器"算：机枪模式用挂点机枪（mg_aa，900 m/s）。
        var ptc = player.GetComponent<PlayerTankController>();
        var w = (ptc != null && ptc.MgMode && player.DeckWeapon != null) ? player.DeckWeapon : player.Weapon;

        // 2026-11-12 用户反馈「鱼雷 Ctrl 瞄准都歪了」：鱼雷弹速只有几十 m/s，
        // 按弹速算飞行时间会得到**上百米**的提前量，准星离目标老远 —— 看着就是"歪"。
        // 慢速/制导武器一律不做提前量，直接指向目标（锁定框画在哪，炮口就指哪）。
        if (w == null || w.isTorpedo || w.projectileSpeed < 300f) return aim;

        float speed = Mathf.Max(150f, w.projectileSpeed);
        float tof = Vector3.Distance(player.transform.position, aim) / speed;
        return aim + vel * tof;
    }
    /// <summary>
    /// 锁定锚点 = VisualCenter（已排除机枪环/粒子/拖尾）。
    /// **框、准星、弹道三者必须用这一个函数**，否则就会出现"瞄船身、打空气"。
    /// 拿不到渲染器时退回 transform.position + 1.2 m。
    /// </summary>
    public static Vector3 AimAnchor(VehicleCombat v)
    {
        if (v == null) return Vector3.zero;
        Vector3 c = VisualCenter(v);
        return c == Vector3.zero ? v.transform.position + Vector3.up * 1.2f : c;
    }

    /// <summary>
    /// 载具的**视觉中心**：把所有启用的渲染器包围盒合并起来取中心。
    /// 比 transform.position / Collider.bounds 更贴近"玩家看到的那条船"，锁定框用它才不会偏。
    /// 拿不到渲染器时返回 Vector3.zero（调用方自己兜底）。
    ///
    /// ⚠️ 2026-11-16 23:05 用户截图："框明显在船上方，船体在框下面"。
    /// 实测炮艇：全部渲染器合并中心 y=7.11（含机枪瞄准环），
    ///           排除 MG_ 后 y=6.75（真实船体中心），差 0.36 m。
    /// 机枪瞄准环 MG_SightRing0~15 / MG_FrontSight 挂在机枪座顶部（dy≈+5.2 m），
    /// 把包围盒上沿从 9.00 拉到 9.72 —— 框就浮在船上面了。
    /// 所以必须排除：粒子/拖尾/线 + **名字以 MG_ 开头的机枪挂件**。
    /// </summary>
    static Vector3 VisualCenter(VehicleCombat v)
    {
        if (v == null) return Vector3.zero;
        // includeInactive=true：包围盒只应由"船有多大"决定，不能由某个部件这一帧
        // 是否 active/是否被隐藏决定（否则锚点会跳，框就飘）。
        var rends = v.GetComponentsInChildren<Renderer>(true);
        bool any = false;
        Bounds b = default;
        foreach (var r in rends)
        {
            if (r == null) continue;
            // ⚠️ 这里**不看 r.enabled**。理由同上：11-17 我在开镜时隐藏炮塔渲染器，
            // 若按 enabled 过滤，同一艘船在开镜/不开镜两种状态下的"视觉中心"会不同 ——
            // 锁定框和弹道锚点会跟着跳。bounds 在 enabled=false 时依然有效。
            if (r is ParticleSystemRenderer) continue;   // 烟/火（冒烟时会把框顶上去）
            if (r is TrailRenderer) continue;
            if (r is LineRenderer) continue;
            if (!string.IsNullOrEmpty(r.name) && r.name.StartsWith("MG_", System.StringComparison.Ordinal))
                continue;                                // 机枪瞄准环/枪管：比船体高出 3 m
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any ? b.center : Vector3.zero;
    }

    void OnGUI()
    {
        if (!Active) return;
        var cam = Camera.main;
        if (cam == null) return;

        float w = Screen.width, h = Screen.height;

        // 锁定中：屏幕四角淡青色渐变（"进入锁定模式"的氛围）
        var col = InRange ? new Color(0.35f, 1f, 0.55f, 0.10f) : new Color(1f, 0.65f, 0.25f, 0.10f);
        if (Target != null) DrawVignette(col);

        if (Target == null)
        {
            var noT = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            noT.normal.textColor = new Color(1f, 1f, 1f, 0.45f);
            GUI.Label(new Rect(0, h * 0.60f, w, 24f), "锁定中 · 视野内没有可锁定的目标", noT);
            return;
        }

        // 2026-11-16 用户："舰炮和鱼雷按 Ctrl 的瞄准有问题 —— 框画偏了，偏上"。
        // 碰撞盒中心 / transform 原点都偏高（船的 transform 在甲板附近），
        // 改用**渲染网格的合并包围盒中心**（真正的视觉中心），框才会套在船身上。
        // 框和弹道必须同一个锚点（AimAnchor = VisualCenter，已排除机枪环）。
        Vector3 aim = AimAnchor(Target);
        Vector3 sp2 = cam.WorldToScreenPoint(aim);
        if (sp2.z <= 0f) return;

        // ⚠️ 2026-11-17 11:10 用户："侧身对着你、船在右方，锁定框会飘到左上角的空气里"。
        //
        // 真因：目标跑到画面外时 `WorldToScreenPoint` 会给出**极其夸张的屏幕坐标**
        // （MCP 实测：目标在正前方偏 0°、150 m 时 x = -5984，屏幕只有 2560 宽），
        // 而这里没有任何范围检查，框就照着 -5984 画到屏幕外/角落去了。
        //
        // 三道防线（都要，缺一个就会再飘）：
        //   ① z > 0 已在上面拦掉"在相机后方"；
        //   ② 这里再拦"投影落在画面外一大截"的情况 —— 不画框，改画边缘箭头；
        //   ③ 距离/夹角也要合理（锁定时目标必然在锥内，但目标会跑）。
        bool onScreen = sp2.x > 0f && sp2.x < w && sp2.y > 0f && sp2.y < h;
        if (!onScreen)
        {
            // 目标跑出画面：不画框（画出去就是"飘到左上角空气"），
            // 只在屏幕边缘给一个指向箭头，告诉玩家目标在哪个方向。
            Vector3 d = aim - cam.transform.position;
            Vector2 dirS = new Vector2(d.x, d.z);
            if (dirS.sqrMagnitude < 0.0001f) return;
            dirS.Normalize();

            float cxE = w * 0.5f, cyE = h * 0.5f;
            float halfW = w * 0.5f - 70f, halfH = h * 0.5f - 70f;
            float tEdge = Mathf.Min(halfW / Mathf.Max(0.001f, Mathf.Abs(dirS.x)),
                                    halfH / Mathf.Max(0.001f, Mathf.Abs(dirS.y)));
            float ex = cxE + dirS.x * tEdge;
            float ey = cyE - dirS.y * tEdge;

            Color ce = InRange ? new Color(0.4f, 1f, 0.6f, 0.85f) : new Color(1f, 0.7f, 0.3f, 0.85f);
            var te = Texture2D.whiteTexture;
            GUI.color = ce;
            GUI.DrawTexture(new Rect(ex - 8f, ey - 8f, 16f, 16f), te);
            GUI.color = Color.white;
            var ste = new GUIStyle(GUI.skin.label)
            { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            ste.normal.textColor = ce;
            GUI.Label(new Rect(ex - 90f, ey + 12f, 180f, 20f), $"目标在视野外 {Distance:F0} m", ste);
            return;
        }

        float px = sp2.x;
        float py = h - sp2.y;

        // ⚠️⚠️ 2026-11-17 11:25 用户截图："明明图片上自己有这个，绿线表示准心和船的范围"。
        //
        // 截图里出现**两个绿框**：一个在船上方、一个在船下方，都不套船。
        // 真因：锁定框被**两处代码各画一遍**：
        //   ① LockOnSystem.OnGUI（这里）—— 裸屏幕像素，**不套 GUI.matrix**
        //   ② MatchHUD.DrawLockCrosshair —— 虚拟画布坐标（_scale 缩放）+ 平滑插值
        // 两个 OnGUI 在同一帧里跑，GUI.matrix 是全局状态：
        // MatchHUD 画完有没有还原 matrix 决定了这里被不被缩放 —— 于是位置随机漂。
        // MCP 实测同一锚点：一处算到屏幕 y=763，另一处 y=572，差 190 px。
        //
        // 修法：**只留 MatchHUD 那套**（它和准星/命中标记同一坐标系统一、还有平滑）。
        // 这里不再画框，只保留「目标跑出画面时的边缘箭头」和距离文字。
        // 见下方 onScreen 分支：那个分支是画在屏幕边缘的，不受缩放影响。

        // 锁定框已交给 MatchHUD.DrawLockCrosshair 画（见上方注释），此处不再重复。
        // 保留：距离 + 射程状态（贴着目标位置显示，字号小，缩放误差看不出来）。
        Color c = InRange ? new Color(0.4f, 1f, 0.6f) : new Color(1f, 0.7f, 0.3f);
        c.a = 0.9f;

        // 中心十字到目标的连线（细、半透明，缩放偏差不可见）
        DrawLine(new Vector2(w * 0.5f, h * 0.5f), new Vector2(px, py), new Color(c.r, c.g, c.b, 0.18f), 1.2f);

        // 距离 + 射程状态
        float size = Mathf.Clamp(22000f / Mathf.Max(60f, Distance), 26f, 120f);
        var st = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        st.normal.textColor = c;
        string rangeTxt = InRange ? $"{Distance:F0} m · 射程内 · 左键发射" : $"{Distance:F0} m · 超出射程";
        GUI.Label(new Rect(px - 140, py + size * 0.6f + 6f, 280, 22f), rangeTxt, st);
    }

    void DrawVignette(Color c)
    {
        float w = Screen.width, h = Screen.height;
        float band = Mathf.Min(w, h) * 0.12f;
        GUI.color = c;
        GUI.DrawTexture(new Rect(0, 0, w, band), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(0, h - band, w, band), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(0, 0, band, h), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(w - band, 0, band, h), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    void DrawBrackets(float cx, float cy, float r, Color c)
    {
        float len = r * 0.42f;
        var t = Texture2D.whiteTexture;
        GUI.color = c;
        // 四个角
        GUI.DrawTexture(new Rect(cx - r, cy - r, len, 2.5f), t);
        GUI.DrawTexture(new Rect(cx - r, cy - r, 2.5f, len), t);
        GUI.DrawTexture(new Rect(cx + r - len, cy - r, len, 2.5f), t);
        GUI.DrawTexture(new Rect(cx + r - 2.5f, cy - r, 2.5f, len), t);
        GUI.DrawTexture(new Rect(cx - r, cy + r - 2.5f, len, 2.5f), t);
        GUI.DrawTexture(new Rect(cx - r, cy + r - len, 2.5f, len), t);
        GUI.DrawTexture(new Rect(cx + r - len, cy + r - 2.5f, len, 2.5f), t);
        GUI.DrawTexture(new Rect(cx + r - 2.5f, cy + r - len, 2.5f, len), t);
        GUI.color = Color.white;
    }

    void DrawLine(Vector2 a, Vector2 b, Color c, float width)
    {
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 1f) return;
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        var m = GUI.matrix;
        GUI.color = c;
        GUIUtility.RotateAroundPivot(ang, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
        GUI.matrix = m;
        GUI.color = Color.white;
    }
}