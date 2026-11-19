using UnityEngine;

/// <summary>
/// 灵魂出窍 / 观战视角（2026-11-17）。
///
/// ================== 设计意图（用户 2026-11-17 17:55 亲口纠正）==================
///
/// 用户原话：
///   "你理解错了我的灵魂出窍按钮……这个灵魂出窍本质上是你没有钱去买新的载具的时候，
///    让你有点游戏体验的，而不是说让你可以用这个方法去拿信息、去当信息位的。"
///
/// 所以**唯一的进入条件**是：
///   ① 你当前**没有载具**（被击毁 / 还没出击）
///   ② 而且你**买不起**最便宜的那台车（钱不够）
///
/// 只有这种情况才能升空 —— 因为此时你什么也做不了，只能等占点收入攒钱，
/// 给个视角让你看着战局不至于干坐着。
///
/// **有车时按 O 不生效**（想观战？那你就是在用信息位，这是设计明确禁止的）。
///
/// ## 配套的经济修正（同一轮改的）
///
/// 为了让"灵魂出窍"真的有意义，同时修了三处：
///   1. 删掉"最便宜的车免费送"（原来 `CanAfford` 里一穷二白就白送轻坦/战斗机）
///   2. 删掉"复活自动补钱"（原来 `OpenGarageForRespawn` 会补一笔刚好够的钱）
///   3. 修 `Eliminated` 语义 —— 原来它被用在**收入过滤**上，导致"死了领不到占点收入"，
///      现在没车也照常领，靠它攒钱买下一台
///
/// 三处合起来：死了 → 没钱 → 不能白拿车 → 灵魂出窍看战局 → 攒够钱 → 出击。
///
/// ## 操作（仿 Minecraft 创造模式）
///
///   W / S          前后（沿视线方向）
///   A / D          左右平移
///   Space          上升
///   Shift          下降
///   Alt（按住）    加速 ×3
///   鼠标            转视角
///   滚轮            调节基础速度
///   O（默认）      退出灵魂状态（回到观战前的机位）。**可在「按键设置」里改**
///
/// ## 为什么不塞进 CameraFollow.cs
///
/// 那个文件已经 1020 行，塞了坦克 / 船 / 飞机三套机位、开镜、机枪分划、滚转跟随。
/// 再往里加"自由飞行"会变成第四个互相打架的入口
/// （2026-11-17 我就因为"没搞懂原有三入口就加第四个"踩过坑，见瞄准链路文档）。
///
/// 做法：**接管相机 transform，但不动 CameraFollow 的任何状态**。
/// 退出时把 target 恢复，CameraFollow 会自己 Lerp 回去。
/// </summary>
public class SoulCamera : MonoBehaviour
{
    public static SoulCamera Instance { get; private set; }

    /// <summary>现在是不是灵魂状态。</summary>
    public static bool Active { get; private set; }

    Camera _cam;
    CameraFollow _follow;

    // 自由飞行的位置 / 朝向
    Vector3 _pos;
    float _yaw, _pitch;
    float _speed = 60f;           // 基础速度（m/s），滚轮可调
    Vector3 _smoothVel;

    // 2026-11-18：玩家按 O 主动退出后，这段时间内不再自动升空。
    // 没有它的话退出当帧条件仍成立 → 立刻被拉回来 → "按 O 没反应"。
    float _manualUntil = -1f;
    const float ManualHoldSeconds = 6f;

    // 这次灵魂视角是不是"从车库按 O 进来的" —— 是的话退出时回车库
    bool _cameFromGarage;

    /// <summary>玩家主动退出后的冷却中（HUD 据此提示"按 O 重新升空"）。</summary>
    public static bool ManualHold => Instance != null && Time.unscaledTime < Instance._manualUntil;

    // 进入前的状态，退出时还原
    Vector3 _savedPos;
    Quaternion _savedRot;
    Transform _savedTarget;
    bool _savedEnabled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("SoulCamera");
        go.AddComponent<SoulCamera>();
        Object.DontDestroyOnLoad(go);
    }

    void Awake()
    {
        Instance = this;
        Active = false;
    }

    MatchManager MM => MatchManager.Instance;

    /// <summary>
    /// 现在能不能用灵魂视角。**核心限制**：
    /// 必须「没有载具」+「买不起任何一台车」两条同时成立。
    /// 有车时按 O 什么都不发生 —— 不允许拿它当信息位。
    ///
    /// 2026-11-18 用户明确要求：**在车库界面按 O 要能进灵魂视角**（这是买不起车时唯一的出路）。
    /// 这条走的是 `GarageScreen` 自己的按钮/O 键 → `EnterFromGarage()`，不经过这里。
    ///
    /// ⚠️ 2026-11-18 晚：车库开着时这里**返回 false**（之前是"车库不算障碍"，
    /// 于是 `Enter()` 会把车库当场关掉）。现在车库开着时由 `GarageScreen` 自己倒计时
    /// 1.5 秒再升空（`GoSoul`）；两边都动手会互相抢，缓冲被跳过。
    /// </summary>
    public static bool CanUse()
    {
        var mm = MatchManager.Instance;
        if (mm == null || mm.matchOver) return false;
        if (!GameStartScreen.Started) return false;
        if (PauseScreen.Paused || ShopScreen.Open) return false;
        if (GarageScreen.Instance != null && GarageScreen.Instance.IsOpen) return false;

        // ① 必须有"没车"这个前提
        var p = mm.Player;
        bool noCar = p == null || !p.Alive;
        if (!noCar) return false;

        // ② 而且**买不起任何一台车** —— 买得起就该去出击，不该在天上看
        //    2026-11-18：用户原话"前提是买不起任何载具才能进灵魂状态"。
        //    用 CanAffordCheapest（含旧车回收后的可用总额）判断，比裸比 Gold 准确。
        return !mm.CanAffordCheapest;
    }

    void Update()
    {
        // 手动退出：O 键 / Esc / G（2026-11-18 用户要求）—— 退出后一律**进车库界面**
        bool wantExit = Bindings.Down(Bindings.Act.Soul)
                     || Input.GetKeyDown(KeyCode.Escape)
                     || Bindings.Down(Bindings.Act.Garage);
        if (Active && wantExit)
        {
            Exit("手动退出");
            _manualUntil = Time.unscaledTime + ManualHoldSeconds;   // 记住"是玩家自己退的"
            _cameFromGarage = false;

            // 2026-11-18 深夜用户要求："退出灵魂视角的意思是按 O 或者 Esc 之后进入商店界面"。
            // 所以不管是从车库上来的还是战场上自动升空的，退出后都进车库（能看价格、能买车）。
            // OpenGarageManual 内部固定 allowAutoSoul:false —— 别再倒计时把人弹回天上。
            var mm0 = MatchManager.Instance;
            if (mm0 != null && mm0.Player == null) mm0.OpenGarageManual();
            return;
        }

        // 满足条件 → 自动进入（此时玩家什么也做不了，给个视角免得干坐着）
        //
        // 2026-11-18 修 bug：原来这里不判 _manualUntil，于是玩家按 O 退出后，
        // 下一帧条件仍然成立（还是没车、还是买不起）→ 立刻又被自动拉回来。
        // 表现就是"按 O 完全没反应"。现在退出后有一段时间不再自动进入，
        // 期间 HUD 会提示"按 O 重新升空"，想回来按一下就行。
        bool hold = Time.unscaledTime < Instance._manualUntil;

        // 冷却期内玩家再按一次 O = 主动收回刚才的"退出"，立刻升空
        if (!Active && hold && Bindings.Down(Bindings.Act.Soul))
        {
            _manualUntil = -1f;
            if (CanUse()) Enter();
            return;
        }

        if (!Active && !hold && CanUse())
        {
            Enter();
            return;
        }

        // 条件不再成立（攒够钱了 / 重新出击了 / 进了 UI）→ 自动退出
        if (Active)
        {
            bool stillAllowed = CanUse();
            // UI 界面只是临时让出相机，不算"条件失效"
            bool inUI = PauseScreen.Paused || ShopScreen.Open
                        || (GarageScreen.Instance != null && GarageScreen.Instance.IsOpen);

            if (!inUI && !stillAllowed)
            {
                Exit("条件不再满足");
                return;
            }
            if (inUI)
            {
                if (_cam != null) _cam.enabled = true;
                return;
            }
        }

        if (!Active || _cam == null) return;

        Look();
        Move();
    }

    // ---------------- 视角 ----------------

    void Look()
    {
        float mx = Input.GetAxisRaw("Mouse X");
        float my = Input.GetAxisRaw("Mouse Y");
        _yaw += mx * 3.0f;
        _pitch = Mathf.Clamp(_pitch - my * 3.0f, -85f, 85f);

        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f)
            _speed = Mathf.Clamp(_speed * (1f + scroll * 0.18f), 10f, 400f);
    }

    void Move()
    {
        // 2026-11-18 深夜：飞行键改成走 Bindings（用户要求把它们放进「按键设置」）。
        // 默认值与驾驶键同键，但玩家可以单独改；HUD 提示也读同一份绑定，不再写死 WASD。
        float h = 0f, v = 0f, up = 0f;
        if (Bindings.Held(Bindings.Act.SoulForward)) v += 1f;
        if (Bindings.Held(Bindings.Act.SoulBack)) v -= 1f;
        if (Bindings.Held(Bindings.Act.SoulRight)) h += 1f;
        if (Bindings.Held(Bindings.Act.SoulLeft)) h -= 1f;
        if (Bindings.Held(Bindings.Act.SoulUp)) up += 1f;
        if (Bindings.Held(Bindings.Act.SoulDown)) up -= 1f;

        float mul = Bindings.Held(Bindings.Act.SoulBoost) ? 3f : 1f;

        var rot = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3 want = (rot * new Vector3(h, 0f, v)).normalized * v * _speed * mul
                     + Vector3.up * up * _speed * mul;

        _smoothVel = Vector3.Lerp(_smoothVel, want, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
        _pos += _smoothVel * Time.unscaledDeltaTime;

        float ground = BattlefieldTerrain.SampleHeight(_pos.x, _pos.z);
        if (_pos.y < ground + 2f) _pos.y = ground + 2f;
        _pos.y = Mathf.Min(_pos.y, 1500f);

        _cam.transform.position = _pos;
        _cam.transform.rotation = rot;
    }

    // ---------------- 进入 / 退出 ----------------

    /// <summary>
    /// 2026-11-18 用户要求：在车库界面按 O 调这个进来。
    ///
    /// 这是**手动**入口，所以不走 `CanUse()`（那个是"自动进入"的限制：
    /// 没车 + 买不起）。玩家主动按 O = 他要弃车观战，只要没暂停/没开商店就放行。
    /// 进入前 `GarageScreen` 已经把车弃掉了，所以进来时确实是"没车"状态。
    /// </summary>
    public void EnterFromGarage()
    {
        if (Active) return;
        if (PauseScreen.Paused || ShopScreen.Open) return;   // 真正不能抢相机的两种情况
        _cameFromGarage = true;
        Enter(true);
    }

    void Enter(bool manual = false)
    {
        if (Active) return;
        if (!manual && !CanUse()) return;   // 自动进入才受"没车+买不起"限制

        _cam = Camera.main;
        if (_cam == null) return;

        // 2026-11-18：万一车库还开着（买不起却没拦住的老路径），先关掉它。
        // 不关的话 Time.timeScale 一直是 0，而且车库那张大面板会整个盖住灵魂视角。
        if (GarageScreen.Instance != null && GarageScreen.Instance.IsOpen)
            GarageScreen.Instance.Close();

        _follow = _cam.GetComponent<CameraFollow>();
        Active = true;

        _savedPos = _cam.transform.position;
        _savedRot = _cam.transform.rotation;
        if (_follow != null)
        {
            _savedTarget = _follow.target;
            _savedEnabled = _follow.enabled;
            _follow.enabled = false;      // 让 CameraFollow 完全不写 transform
        }

        _pos = _savedPos + Vector3.up * 8f;
        _yaw = _cam.transform.eulerAngles.y;
        _pitch = 35f;
        _smoothVel = Vector3.zero;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        Debug.Log("[SoulCamera] 进入灵魂视角（没有载具且买不起车）· " + HintText);
    }

    public void Exit(string reason)
    {
        if (!Active) return;
        Active = false;

        if (_follow != null)
        {
            // 2026-11-18 晚修：退出时**优先跟场上那台真车**。
            // 原来无条件 `target = _savedTarget`（进灵魂前的值，没车时是 null）。
            // 如果玩家是在灵魂状态下从车库买了车，SpawnUnit 已经把 target 指到新车，
            // 这里再还原成 null 会让相机跟不到新车 —— 新车上场，镜头却是死的。
            var mm = MatchManager.Instance;
            var p = mm != null ? mm.Player : null;
            _follow.target = p != null ? p.transform : _savedTarget;
            _follow.enabled = _savedEnabled;
        }

        Debug.Log($"[SoulCamera] 退出灵魂视角（{reason}）");
    }

    /// <summary>
    /// HUD 用：一行操作提示。
    /// **每个键都必须动态读 Bindings** —— 玩家在「按键设置」里改了灵魂视角的键，
    /// 这里的提示要跟着变，否则玩家按提示的键没反应（2026-11-17/11-18 用户两次交代）。
    /// </summary>
    public static string HintText
    {
        get
        {
            string f = Bindings.NameOf(Bindings.Act.SoulForward);
            string b = Bindings.NameOf(Bindings.Act.SoulBack);
            string l = Bindings.NameOf(Bindings.Act.SoulLeft);
            string r = Bindings.NameOf(Bindings.Act.SoulRight);
            string up = Bindings.NameOf(Bindings.Act.SoulUp);
            string dn = Bindings.NameOf(Bindings.Act.SoulDown);
            string boost = Bindings.NameOf(Bindings.Act.SoulBoost);
            string exit = Bindings.NameOf(Bindings.Act.Soul);
            return $"灵魂视角 · {f}/{l}/{b}/{r} 平飞 / {up} 升 / {dn} 降 / {boost} 加速 / 滚轮调速 / {exit} 退出";
        }
    }
}
