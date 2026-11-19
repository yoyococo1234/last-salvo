using UnityEngine;

/// <summary>
/// 车库：开局和每次阵亡后在这里选载具（花金币买）。
/// 打开时游戏暂停（Time.timeScale = 0）、光标放开；选完自动关闭并恢复。
/// 样式走 UiKit：深蓝底 + 蓝描边卡片 + 金色强调，与开局封面一致。
/// </summary>
public class GarageScreen : MonoBehaviour
{
    public static GarageScreen Instance { get; private set; }

    public bool IsOpen { get; private set; }

    string _title = "选择载具";
    string _subtitle = "";
    float _anim;                 // 开合进度（0 = 关 / 1 = 开），给淡入淡出和弹出用

    // 2026-11-18：旧车回收的 +N 浮动动画（金币格右侧，播一次就消失）
    float _tradeInAt = -99f;
    int _tradeInShown;

    // 2026-11-18 晚（用户要求）：**买不起任何载具时不要在车库里干等**。
    // 旧版只在这里写一句"还差 91 金 · 点左上「退出」回战场"，玩家还得自己绕一圈；
    // 现在给 1.5 秒看清价格，然后自动升空观战（急了按 O 或点「升空观战」立刻走）。
    const float AutoSoulDelay = 1.5f;
    float _autoSoulAt = -1f;      // 排程时刻（-1 = 没排）
    bool _wantSoul;               // 点了「升空观战」按钮（下一帧 Update 执行）
    bool _allowAutoSoul = true;   // false = 玩家刚从灵魂视角主动退出，别再自动弹回去
    float _confirmQuitAt = -99f;  // 左上「退出本局」的二次确认（3 秒内再点才真退）

    void Awake() => Instance = this;

    void Update()
    {
        if (!IsOpen) return;

        var mm = MatchManager.Instance;

        // 点了「升空观战」按钮：OnGUI 里只置位，真正关库/升空在这里做
        if (_wantSoul) { _wantSoul = false; GoSoul(); return; }

        // ---- 没钱买任何载具 → 自动升空观战（2026-11-18 晚，用户要求）----
        // 用户原话："没钱买就进入灵魂模式，别和现在一样还说一声"。
        // 所以这里不再留一行"点左上退出回战场"，而是**真的把人送上去**：
        //   · 1.5 秒缓冲：够看清六张卡的价格和还差多少金
        //   · 等不及就点右边「升空观战」或按 O
        // ⚠️ 必须用 unscaledTime —— 车库开着时 Time.timeScale = 0。
        bool brokeNoCar = mm != null && mm.Player == null && !mm.CanAffordCheapest;
        if (brokeNoCar && _allowAutoSoul)
        {
            // 玩家刚按 O 从灵魂视角退回来：这 6 秒内别自动把他拉回去（否则"按 O 没反应"）
            if (SoulCamera.ManualHold) _autoSoulAt = -1f;
            else if (_autoSoulAt < 0f) _autoSoulAt = Time.unscaledTime + AutoSoulDelay;
            else if (Time.unscaledTime >= _autoSoulAt) { GoSoul(); return; }
        }
        else _autoSoulAt = -1f;

        // 2026-11-18 用户要求：**按 O 直接进灵魂模式**，不要再点「退出」绕一圈。
        //
        // 语义：按 O = "我主动弃车去观战"。
        //   · 有车 → 当场弃置（回收金照样进钱包），然后升空
        //   · 没车 → 直接升空
        // 不再要求"必须买不起"——那是**自动**进入的限制，手动按 O 是玩家自己选的。
        if (Bindings.Down(Bindings.Act.Soul))
        {
            if (mm != null && mm.Player != null) mm.AbandonCurrentVehicle();
            GoSoul();
            return;
        }

        // 2026-11-18 深夜（用户："按键很混乱"）—— 把按键语义彻底拆开，各管一件事：
        //   O / 「升空观战」 = 弃车 + 升空观战（上面已处理）
        //   Esc            = **只是关掉车库**回到战场，车还在就继续开（不再顺手弃车）
        //   ← 退出本局     = 结束这一局回主菜单（两次点击确认）
        // 原来 Esc 等于"弃车"，和 O 撞在一起，玩家按哪个都像在弃车。
        //
        // 2026-11-18 深夜再修（用户："我在开始进入游戏的时候按了一下 esc，
        // 理论上说我现在还没开始，应该是退出效果，但是跟我说我坠毁"）：
        // 开局那次车库里按 Esc 必须等于**退出这一局**，不能是"关掉车库"——
        // 因为此时还没出击、场上根本没车，"关掉车库"回战场什么也干不了，
        // 还会被"攒够钱自动弹库"立刻拉回来（表现就是"我还没打呢，你就说我载具被击毁"）。
        if (!Input.GetKeyDown(KeyCode.Escape)) return;
        UiSound.Play(UiSound.Kind.Close);
        if (mm != null && !mm.HasDeployed)
        {
            mm.Restart();                  // 还没出击 → Esc = 退出本局，回主菜单
            return;
        }
        if (mm != null) mm.CancelGarageSwap();     // 清掉按 G 时挂的旧车回收账（车没卖）
        Close();
    }

    public void Open(string title, string subtitle = "", bool allowAutoSoul = true)
    {
        _title = title;
        _subtitle = subtitle;
        IsOpen = true;
        _autoSoulAt = -1f;
        _wantSoul = false;
        _confirmQuitAt = -99f;        // 每次开库都清掉"确认退出"的待确认状态
        // 2026-11-18 深夜：从灵魂视角**主动退出**进来的，不自动升空（见 MatchManager.OpenGarageManual）。
        _allowAutoSoul = allowAutoSoul;

        // 2026-11-18：进库时播一次「旧车 +N」的浮动动画（没有旧车就不播）
        var mm0 = MatchManager.Instance;
        int ti = mm0 != null ? mm0.TradeInValue : 0;
        if (ti > 0) { _tradeInShown = ti; _tradeInAt = Time.unscaledTime; }
        else { _tradeInShown = 0; }

        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        UiSound.Play(UiSound.Kind.Open);
    }

    public void Close()
    {
        IsOpen = false;
        _autoSoulAt = -1f;
        _wantSoul = false;
        Time.timeScale = 1f;
        if (GameStartScreen.Started)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        // 关掉之后这一小段时间继续吞鼠标：否则点「出击」那一下左键还按着，车一出来就开一炮
        UiKit.SwallowInput(0.35f);
    }

    /// <summary>
    /// 关车库 + 升空观战（2026-11-18 晚）。
    /// 按 O / 点「升空观战」/ 没钱自动倒计时到点 三个入口都走这里，
    /// 保证"关车库（timeScale 回 1）→ 进灵魂视角"的顺序不会各写一份走歪。
    /// </summary>
    public void GoSoul()
    {
        _autoSoulAt = -1f;
        UiSound.Play(UiSound.Kind.Open);
        Close();                                    // 关车库：timeScale 回 1，占点收入恢复
        if (SoulCamera.Instance != null) SoulCamera.Instance.EnterFromGarage();
    }

    // 卡片里数值条的满格参考（只用于画条，不影响玩法）
    const float HpRef = 12000f;
    const float SpeedRef = 80f;

    // 车库背景：实景暮色图（低分辨率渲的，靠双线性放大出虚化景深），没有就退回渐变
    static Texture2D _bg;
    static bool _bgTried;

    static Texture2D Bg()
    {
        if (!_bgTried) { _bgTried = true; _bg = Resources.Load<Texture2D>("UiBackdrops/garage"); }
        return _bg;
    }

    void OnGUI()
    {
        // 开合动画：关掉以后还要多画 0.15 秒淡出，所以先推进进度再决定要不要画
        float k = UiKit.Anim(ref _anim, IsOpen);
        if (!IsOpen && k <= 0.001f) return;

        var mm = MatchManager.Instance;
        if (mm == null) return;

        var oldMatrix = UiKit.BeginScaled(out float scale, out float vw);
        var mouse = UiKit.Mouse(scale);
        bool live = IsOpen;                  // 淡出过程中不再响应点击

        UiKit.Fade(k);
        // 实景暮色背景（虚化）：压暗得多一些，卡片才压得住
        UiKit.PhotoBackdrop(vw, Bg(), 0.62f, 0.58f, 0.88f, 0.40f);

        // 2026-11-18 深夜（用户对着「你的金币 630 · 最便宜 147」说"这两行写了有鸟用"）：
        // 副标题**整个不再显示**。金币格已经写着「金币 630」（有旧车折价时还写"可用 X / 630+70"），
        // 每张车卡右下角写着「还差 N 金」—— 再在标题下面重复一遍纯属噪音。
        float y = UiKit.Heading(vw, 34f, _title, "");

        // 买不起时给一个「升空观战」按钮（放右上角，和「空战载具 N 分钟后开放」错开一行）
        bool broke = mm.Player == null && !mm.CanAffordCheapest;
        if (broke)
        {
            var br = new Rect(vw - 24f - 210f, 84f, 210f, 38f);
            var hb = UiKit.Button("garage_soul", br, mouse, live);
            var rb = UiKit.Lift(br, hb);
            bool hot = hb.hover01 > 0.4f;
            UiKit.Box(rb, hot ? new Color(0.35f, 0.75f, 1f, 0.22f) : new Color(0.055f, 0.070f, 0.100f, 0.90f));
            UiKit.Frame(rb, hot ? UiKit.Ally : UiKit.Edge);
            UiKit.Label(rb, "买不起 · 升空观战 ›", 17, hot ? UiKit.Text : UiKit.Ally, TextAnchor.MiddleCenter, true);
            // 点击真实动作放到下一帧 Update 里做：OnGUI 中途 return 会把 GUI.matrix 留歪
            if (hb.clicked) _wantSoul = true;
        }

        // ---- 左上角「退出本局」（2026-11-18 深夜按用户要求重定义）----
        // 用户原话："左上角的退出功能好像有问题啊，现在我们需要用这个按键表示退出啊，退出游戏啊"
        //
        // 以前这个按钮语义是混的：有车→弃车、开局没车→重开、中途没车→干脆不给退。
        // 现在统一成**就是退出**：无条件退出这一局、回主菜单（重载场景 → GameStartScreen.Awake
        // 把 Started 置回 false，封面重新出现）。
        //
        // 弃车不再挂在这个按钮上：要弃车去观战走 O / 「升空观战」；要取消换车走 Esc。
        // 退出整局是破坏性操作，做成**两次点击确认**（3 秒内再点才退），避免误触丢进度。
        {
            bool armed = Time.unscaledTime - _confirmQuitAt < 3f;
            var r0 = new Rect(24f, 26f, armed ? 210f : 168f, 52f);
            var h0 = UiKit.Button("garage_exit", r0, mouse, live);
            var rr = UiKit.Lift(r0, h0);
            bool hot = h0.hover01 > 0.4f || armed;
            UiKit.Box(rr, hot ? new Color(1f, 0.42f, 0.36f, 0.20f) : new Color(0.055f, 0.070f, 0.100f, 0.88f));
            UiKit.Frame(rr, armed ? UiKit.Bad : (hot ? UiKit.Bad : UiKit.Edge));
            UiKit.Label(rr, armed ? "确认退出本局？" : "← 退出本局", 20,
                (hot || armed) ? UiKit.Bad : UiKit.Desc, TextAnchor.MiddleCenter, true);
            if (h0.clicked)
            {
                if (armed)
                {
                    _confirmQuitAt = -99f;
                    UiSound.Play(UiSound.Kind.Close);
                    // 先复位缩放矩阵再重载场景：Restart() 里是 LoadScene，本帧不会自己走到
                    // 末尾那两句复位，GUI.matrix 会带着缩放污染后面的绘制。
                    GUI.matrix = oldMatrix;
                    UiKit.EndScreen();
                    mm.Restart();           // 重载场景 = 回主菜单（封面重新出现，一切重置）
                    return;
                }
                _confirmQuitAt = Time.unscaledTime;
                UiSound.Play(UiSound.Kind.Click);
            }
        }

        // ---- 买不起的出路：已经在副标题里说明（见上面），这里不再重复画横条 ----

        // ---- 海空解锁倒计时（开局只玩坦克）----
        {
            // 2026-11-11：海战常开，只有空战要等
            float airIn = MatchSettings.UnlockIn(Domain.Air);
            string note = null;
            if (airIn > 0f) note = $"空战载具 {Mathf.CeilToInt(airIn / 60f)} 分钟后开放";
            else if (MatchSettings.UnlockMinutes > 0) note = "空战载具已开放";
            if (note != null)
            {
                var nr = new Rect(vw - 24f - 360f, 34f, 360f, 40f);
                UiKit.Box(nr, new Color(0.055f, 0.070f, 0.100f, 0.88f));
                UiKit.Frame(nr, airIn > 0f ? UiKit.Gold : UiKit.Ally);
                UiKit.Label(nr, note, 17, airIn > 0f ? UiKit.Gold : UiKit.Ally, TextAnchor.MiddleCenter, true);
            }
        }

        // ---- 状态条：四个小格 ----
        // 2026-11-17：命数机制删除，"剩余部署 / 我方剩余命"换成"我方胜利点 / 敌方胜利点"
        //
        // 2026-11-18 深夜（用户连拍两张图："数字全都对不上"）：
        // 判定"买不买得起"用的是 `SpendableGold = 金币 + 旧车回收`，但界面上**只显示金币**，
        // 那笔回收只在开库时飘 1.6 秒就消失。玩家看到「金币 29 / 轻坦 170 / 还差 60」
        // （170-29=141）当然算不对 —— 真实可用是 29+81=110，110+60=170 才对。
        // 修法：金币格改成**常驻显示可用总额 + 明细**，让玩家自己能验算。
        int tradeInNow = mm.TradeInValue;
        bool hasTradeIn = tradeInNow > 0;
        string[] statK = { hasTradeIn ? "可用金币" : "金币", "我方胜利点", "敌方在场", "敌方胜利点" };
        string[] statV = { mm.SpendableGold.ToString(), mm.Vp0.ToString(), $"{mm.EnemiesLeft} 台", mm.Vp1.ToString() };
        Color[] statC = { UiKit.Gold, UiKit.Ally, UiKit.Bad, UiKit.Bad };
        float sw = 200f, sg = 14f;
        float sx = vw * 0.5f - (sw * 4f + sg * 3f) * 0.5f;
        float sy = y + 6f;
        for (int i = 0; i < 4; i++)
        {
            var r = new Rect(sx + i * (sw + sg), sy, sw, 54f);
            UiKit.Box(r, new Color(0.055f, 0.070f, 0.100f, 0.80f));
            UiKit.Frame(r, UiKit.Edge);
            UiKit.Label(new Rect(r.x + 14f, r.y + 4f, r.width - 28f, 22f), statK[i], 15, UiKit.Dim);

            if (i == 0 && hasTradeIn)
            {
                // 值 = 可用总额，右边小字 = "金币 + 旧车折价"，一眼能验算
                UiKit.Label(new Rect(r.x + 14f, r.y + 22f, 78f, 30f), statV[i], 22, UiKit.Gold, TextAnchor.MiddleLeft, true);
                UiKit.Label(new Rect(r.x + 88f, r.y + 27f, r.width - 96f, 24f),
                    $"{mm.Gold}+{tradeInNow}", 14, UiKit.Dim, TextAnchor.MiddleLeft);
            }
            else
            {
                UiKit.Label(new Rect(r.x + 14f, r.y + 22f, r.width - 28f, 30f), statV[i], 22, statC[i], TextAnchor.MiddleLeft, true);
            }

            // 金币格右侧：旧车回收 +N 的浮动动画（2026-11-18 用户要求；明细已常驻，动画只作强调）
            if (i == 0 && _tradeInShown > 0)
            {
                float age = Time.unscaledTime - _tradeInAt;
                const float dur = 1.6f;
                if (age < dur)
                {
                    float tick = Mathf.Clamp01(age / dur);           // 注意别叫 k，外层开合动画已占用
                    float a = 1f - UiKit.Smooth(tick);               // 淡出
                    float dy = -UiKit.Smooth(tick) * 26f;            // 向上飘 26px
                    var tr = new Rect(r.x + r.width * 0.52f, r.y + 20f + dy, r.width * 0.46f, 34f);
                    UiKit.Label(tr, $"+{_tradeInShown}", 24,
                        new Color(0.55f, 1f, 0.6f, a), TextAnchor.MiddleRight, true);
                }
                else _tradeInShown = 0;
            }
        }

        // ---- 载具卡片 ----
        var roster = mm.Roster;
        if (roster != null && roster.Length > 0)
        {
            int cols = 3;
            // 卡片高度 330 → 370：顶部让出 120 给载具缩略图（2026-11-10）
            float cw = 400f, ch = 370f, gap = 24f;
            float totalW = cols * cw + (cols - 1) * gap;
            float x0 = vw * 0.5f - totalW * 0.5f;
            float y0 = sy + 80f;

            for (int i = 0; i < roster.Length; i++)
            {
                var def = roster[i];
                if (def == null) continue;

                int col = i % cols, row = i / cols;
                // slot 是命中区（不随动画移动），r 是实际画出来的位置（悬停上浮 / 按下下沉 / 拒绝抖动）
                var slot = new Rect(x0 + col * (cw + gap), y0 + row * (ch + gap), cw, ch);

                bool unlocked = MatchSettings.DomainUnlocked(def.domain);   // 2026-11-11：开局只玩坦克
                // 2026-11-18 用户定稿：换车 = 旧车折成金币 + 按正常价买新车。
                // 所以卡片显示**正常车价**（不再抵扣），旧车那笔单独标在右边。
                int actualPrice = mm.PriceFor(def);
                int need = actualPrice - mm.SpendableGold;      // 还差多少金（≤0 = 买得起）
                bool afford = unlocked && need <= 0;
                bool free = afford && actualPrice <= 0;   // PriceFor 最小返回 1，恒 false，保留兜底

                var hit = UiKit.Button("garage_" + def.displayName, slot, mouse, afford && live);
                var r = UiKit.Lift(slot, hit);
                float hv = hit.hover01;
                bool hover = hv > 0.5f;

                UiKit.Glow(r, hv, UiKit.Gold);        // 悬停外发光，画在卡片下面
                UiKit.Card(r, hover);
                if (!afford) UiKit.Box(r, new Color(0f, 0f, 0f, 0.35f));

                float ix = r.x + 22f, iw = r.width - 44f;
                Color nameC = afford ? UiKit.Text : UiKit.Mute;

                // ---- 载具缩略图（2026-11-10）----
                // 六张纯文字卡片看着像表格，加一张车图立刻变成"游戏界面"。
                // 图是 Assets/Editor/UiThumbGen.cs 渲出来的透明底 PNG，底部压一层渐变融进卡片底色。
                var band = new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 120f);
                DrawThumb(band, def, afford, hover);

                // 名称 + 域标签
                UiKit.Label(new Rect(ix, r.y + 126f, iw - 90f, 36f), def.displayName, 26, nameC, TextAnchor.MiddleLeft, true);
                UiKit.Chip(new Rect(r.xMax - 22f - 76f, r.y + 130f, 76f, 26f), DomainText(def.domain), DomainColor(def.domain), 15);

                UiKit.HLine(ix, r.y + 168f, iw);

                // 两条数值
                StatRow(ix, r.y + 178f, iw, "血量", $"{def.maxHp:F0}", def.maxHp / HpRef, afford);
                StatRow(ix, r.y + 208f, iw, "速度", $"{def.speed:F0} m/s", def.speed / SpeedRef, afford);

                // 武器
                string weapon = "无武器";
                if (def.weapons != null && def.weapons.Length > 0 && def.weapons[0] != null)
                {
                    weapon = $"{def.weapons[0].displayName}（{def.weapons[0].baseDamage:F0} 伤害 / {def.weapons[0].range:F0} m）";
                    if (def.weapons.Length > 1 && def.weapons[1] != null)
                    weapon += $" + {def.weapons[1].displayName}";
                }
                // 武器（最多两行：主炮 + 同轴机枪这种长串会换行，所以给它 36 px 高）
                UiKit.Label(new Rect(ix, r.y + 236f, iw, 36f), weapon, 15, afford ? UiKit.Desc : UiKit.Mute);

                // 简介
                UiKit.Label(new Rect(ix, r.y + 274f, iw, 34f), def.description, 15,
                    afford ? UiKit.Dim : UiKit.Mute, TextAnchor.UpperLeft);

                // 底部价格栏
                var foot = new Rect(r.x + 1f, r.yMax - 58f, r.width - 2f, 57f);
                UiKit.Box(foot, hover ? new Color(1f, 0.84f, 0.35f, 0.14f) : new Color(1f, 1f, 1f, 0.03f));
                UiKit.HLine(r.x + 1f, foot.y, r.width - 2f, hover ? 0.0f : 0.13f);
                if (hover) UiKit.Box(new Rect(r.x + 1f, foot.y, r.width - 2f, 1f), new Color(1f, 0.84f, 0.35f, 0.45f));

                // 2026-11-18：显示实际车价（绝不再是 0）。
                // 用户要求：旧车回收**不在这里写**，改成进库时金币格旁边播一次 +N 动画。
                string priceTxt = !unlocked ? "未开放"
                                : free ? "免费" : $"￥ {actualPrice}";
                UiKit.Label(new Rect(ix, foot.y, iw * 0.5f, foot.height), priceTxt, 24,
                    unlocked && afford ? UiKit.Gold : UiKit.Mute, TextAnchor.MiddleLeft, true);

                string rightTxt;
                Color rightC;
                if (!unlocked)
                {
                    int mins = Mathf.CeilToInt(MatchSettings.UnlockIn(def.domain) / 60f);
                    rightTxt = $"{mins} 分钟后开放";
                    rightC = UiKit.Gold;
                }
                else if (afford) { rightTxt = hover ? "点击出击 ›" : "出击"; rightC = hover ? UiKit.Gold : UiKit.Desc; }
                // 2026-11-18 晚用户要求：买不起要**明确提醒差多少**，而且点不动（不让选）。
                else { rightTxt = $"还差 {Mathf.Max(1, need)} 金"; rightC = UiKit.Bad; }

                UiKit.Label(new Rect(ix + iw * 0.5f, foot.y, iw * 0.5f, foot.height), rightTxt, 18,
                    rightC, TextAnchor.MiddleRight, hover && afford);

                if (hit.clicked)
                {
                    mm.DeployPlayer(def);
                }
                else if (hit.denied)
                {
                    // 点不起 / 没开放：卡片已经抖过了，再补一句浮字说明原因
                    UiKit.Float(unlocked ? $"还差 {Mathf.Max(1, need)} 金" : "该域还没开放", new Vector2(r.center.x, r.y + 46f),
                        unlocked ? UiKit.Bad : UiKit.Gold);
                }
            }
        }
        else
        {
            UiKit.Label(new Rect(0, 420f, vw, 40f), "没有可选载具（MatchDef.roster 为空，先跑菜单 ②）", 20, UiKit.Bad, TextAnchor.MiddleCenter);
        }

        UiKit.EndScreen();          // 涟漪 + 浮字压在最上层，并复位透明度
        GUI.matrix = oldMatrix;
    }

    static void StatRow(float x, float y, float w, string k, string v, float t, bool on)
    {
        UiKit.Label(new Rect(x, y, 52f, 24f), k, 15, on ? UiKit.Dim : UiKit.Mute);
        UiKit.Bar(new Rect(x + 56f, y + 9f, w - 56f - 110f, 6f), t,
            on ? new Color(0.35f, 0.75f, 1f, 0.85f) : new Color(1f, 1f, 1f, 0.18f));
        UiKit.Label(new Rect(x + w - 104f, y, 104f, 24f), v, 16, on ? UiKit.Text : UiKit.Mute, TextAnchor.MiddleRight, true);
    }

    // ---------------- 载具缩略图（2026-11-10） ----------------

    static readonly System.Collections.Generic.Dictionary<string, Texture2D> _thumbs =
        new System.Collections.Generic.Dictionary<string, Texture2D>();

    /// <summary>
    /// 按 VehicleDef 的资产名取缩略图（`Assets/Resources/UiThumbs/&lt;资产名&gt;.png`）。
    /// 找不到也缓存 null —— 否则每帧 OnGUI 都会去 Resources.Load 一次。
    /// </summary>
    static Texture2D Thumb(VehicleDef def)
    {
        if (def == null) return null;
        if (_thumbs.TryGetValue(def.name, out var t)) return t;
        t = Resources.Load<Texture2D>("UiThumbs/" + def.name);
        _thumbs[def.name] = t;
        return t;
    }

    /// <summary>卡片顶部那块车图：背光 + ScaleToFit 保比例 + 底边渐变让它"站"在卡片上。</summary>
    static void DrawThumb(Rect band, VehicleDef def, bool afford, bool hover)
    {
        // 背光：按域染色的径向光晕，车才从深色底上"浮"出来（2026-11-10）
        var dc = DomainColor(def.domain);
        UiKit.Blob(new Rect(band.center.x - band.width * 0.52f, band.center.y - band.height * 0.72f,
            band.width * 1.04f, band.height * 1.50f), new Color(dc.r, dc.g, dc.b, 0.30f));

        var tex = Thumb(def);
        if (tex != null)
        {
            var old = GUI.color;
            // 买不起的车把图压暗一点（配合卡片上那层黑罩，一眼能看出"不可选"）
            GUI.color = new Color(1f, 1f, 1f, (afford ? 1f : 0.55f) * UiKit.Alpha);
            GUI.DrawTexture(band, tex, ScaleMode.ScaleToFit, true);
            GUI.color = old;

            // 底边渐变收到卡片底色里。**底色要跟着 hover 变**，否则悬停时卡片变亮、
            // 这条渐变还是暗的，会出现一道可见的横带。
            // 用 FadeUp（单张贴图一次画）而不是逐条 VGradient —— 后者在 1.333 缩放下有接缝条纹。
            var baseC = hover ? UiKit.PanelHover : UiKit.PanelBg;
            UiKit.FadeUp(new Rect(band.x, band.yMax - 30f, band.width, 30f),
                new Color(baseC.r, baseC.g, baseC.b, 0.95f));
        }
        else
        {
            // 还没跑 `Tools → 最后校射 → 生成载具缩略图` 时的占位，别让卡片塌掉
            UiKit.Label(new Rect(band.x, band.y, band.width, band.height), "（无预览图）", 15,
                new Color(1f, 1f, 1f, 0.28f), TextAnchor.MiddleCenter);
        }
    }

    static string DomainText(Domain d)
        => d == Domain.Sea ? "海战" : d == Domain.Air ? "空战" : "陆战";

    static Color DomainColor(Domain d)
        => d == Domain.Sea ? new Color(0.35f, 0.75f, 1f) : d == Domain.Air ? new Color(0.70f, 0.62f, 1f) : new Color(0.62f, 0.85f, 0.45f);
}
