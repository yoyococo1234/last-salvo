using UnityEngine;
using System.IO;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 演示视频镜头编排 v2：主动触发开火 + 修好的相机跟随。
///
/// v1 的问题：
///   ① 坦克段没有开火 —— 只跟着 AI 拍，AI 未必在开火窗口，画面很"平"。
///      现在每段都主动调 `FireAt()` 往目标打，保证有炮口火光、弹道、爆炸。
///   ② 飞机段拍到山体侧面 —— 相机 `LookAt(车前方40m)` 在高速飞行时滞后太多。
///      现在改成直接跟机身朝向 + 更大的前视距离。
///
/// 用法：ShotDirector.Start(ShotDirector.Plan.All);
/// </summary>
public class ShotDirector : MonoBehaviour
{
    public static ShotDirector Instance;
    public static string OutDir = "D:/_tmp_video_test/shots2";

    public enum Plan { All, TankOnly, AirOnly, SeaOnly }

    Camera _cam;
    RenderTexture _rt;
    Texture2D _tex;
    readonly List<Behaviour> _hidden = new List<Behaviour>();
    int _frameNo;
    int _fps = 30;
    bool _running, _done;
    Plan _plan = Plan.All;

    public bool IsRunning => _running;
    public bool IsDone => _done;
    public int FrameNo => _frameNo;

    public static ShotDirector Start(Plan plan = Plan.All)
    {
        if (Instance != null && Instance._running) return Instance;
        var go = new GameObject("__ShotDirector");
        var d = go.AddComponent<ShotDirector>();
        d._plan = plan;
        return d;
    }

    void Awake() { Instance = this; }
    void Start() { StartCoroutine(Run()); }

    // ---------------- 基础设施 ----------------

    void HideHUD()
    {
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null) continue;
            string n = mb.GetType().Name;
            if (n == "MatchHUD" || n == "LockOnSystem" || n == "PauseScreen"
                || n == "ShopScreen" || n == "GarageScreen" || n == "PerfProbe")
            {
                if (mb.enabled) { mb.enabled = false; _hidden.Add(mb); }
            }
        }
    }

    void RestoreHUD()
    {
        foreach (var b in _hidden) if (b != null) b.enabled = true;
        _hidden.Clear();
    }

    void SetupCam()
    {
        const int W = 1920, H = 1080;
        _rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
        _rt.antiAliasing = 4;
        _rt.Create();
        _tex = new Texture2D(W, H, TextureFormat.RGB24, false);

        var go = new GameObject("__ShotCam");
        _cam = go.AddComponent<Camera>();
        _cam.clearFlags = CameraClearFlags.Skybox;
        _cam.fieldOfView = 50f;
        _cam.nearClipPlane = 0.5f;
        _cam.farClipPlane = 4000f;
        _cam.targetTexture = _rt;
        _cam.depth = 100;
    }

    void SaveFrame()
    {
        RenderTexture.active = _rt;
        _tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        _tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(Path.Combine(OutDir, "s" + _frameNo.ToString("D5") + ".png"),
                           _tex.EncodeToPNG());
        _frameNo++;
    }

    /// <summary>
    /// 跟随某台载具并持续开火。
    /// 相机：在车身后方 dist 米、高 height 米，看向"车前方 lookAhead 米 + 抬高 2m"。
    /// 开火：每 fireEvery 秒调一次 FireAt(目标点)，目标优先打最近的敌人。
    /// </summary>
    IEnumerator FollowAndFire(VehicleCombat v, float seconds, float dist, float height,
                              float lookAhead = 45f, float fireEvery = 0.9f, float side = 0f)
    {
        int n = Mathf.RoundToInt(seconds * _fps);
        Vector3 smoothPos = v != null ? v.transform.position : Vector3.zero;
        float nextFire = 0f;

        for (int i = 0; i < n; i++)
        {
            if (v == null || !v.Alive) break;

            Vector3 fwd = v.transform.forward;
            Vector3 right = v.transform.right;
            Vector3 want = v.transform.position - fwd * dist + Vector3.up * height + right * side;

            // 跟随机身朝向，但位置平滑（避免抖动）
            smoothPos = Vector3.Lerp(smoothPos, want, 0.18f);
            _cam.transform.position = smoothPos;
            Vector3 look = v.transform.position + fwd * lookAhead + Vector3.up * 2.5f;
            _cam.transform.rotation = Quaternion.Slerp(_cam.transform.rotation,
                Quaternion.LookRotation(look - smoothPos), 0.22f);

            // 主动开火
            if (Time.time >= nextFire)
            {
                nextFire = Time.time + fireEvery;
                var tgt = NearestEnemy(v);
                Vector3 aim = tgt != null ? tgt.transform.position + Vector3.up * 1.5f
                                          : v.transform.position + fwd * 400f;
                v.FireAt(aim);
                v.FireSecondary();
            }

            yield return new WaitForEndOfFrame();
            SaveFrame();
        }
    }

    /// <summary>找离 v 最近的敌方载具。</summary>
    VehicleCombat NearestEnemy(VehicleCombat v)
    {
        VehicleCombat best = null;
        float bestD = float.MaxValue;
        foreach (var o in Object.FindObjectsByType<VehicleCombat>(FindObjectsSortMode.None))
        {
            if (o == null || !o.Alive || o.team == v.team) continue;
            float d = Vector3.Distance(o.transform.position, v.transform.position);
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    VehicleCombat Pick(string defId, int team = 0)
    {
        foreach (var v in Object.FindObjectsByType<VehicleCombat>(FindObjectsSortMode.None))
            if (v != null && v.Alive && v.Def != null && v.Def.id == defId && v.team == team)
                return v;
        return null;
    }

    /// <summary>把目标挪到载具正前方一定距离，保证画面里能看到它被打。</summary>
    void StageTargetAhead(VehicleCombat self, VehicleCombat tgt, float dist)
    {
        if (self == null || tgt == null) return;
        Vector3 p = self.transform.position + self.transform.forward * dist;
        p.y = Mathf.Max(p.y, 6f);
        tgt.transform.position = p;
    }

    // ---------------- 主流程 ----------------

    IEnumerator Run()
    {
        _running = true; _done = false;
        Directory.CreateDirectory(OutDir);
        foreach (var f in Directory.GetFiles(OutDir, "*.png")) File.Delete(f);

        HideHUD();
        SetupCam();
        var fogWas = RenderSettings.fog;
        RenderSettings.fog = false;

        // ⚠️ 2026-11-19 **关键修复**：为什么 v2 的实机片段看起来"飞快"（约 9.5 倍速）？
        //
        // 抓帧循环里每帧都要 `EncodeToPNG()` + 写盘，一帧要 200~300 ms。
        // 而游戏逻辑是按**真实时间**跑的 —— 于是"游戏里过了 5 分钟"，我们只抓到 948 帧，
        // 按 30fps 编码就变成 31.6 秒，等于把 5 分钟压缩成半分钟 = 9.5 倍速。
        //
        // 正解：`Time.captureFramerate = 30` —— 这是 Unity 给离线渲染准备的开关，
        // 它让 `Time.deltaTime` 恒等于 1/30 秒，**与真实渲染耗时脱钩**。
        // 这样无论每帧渲染多久，游戏内 1 秒 = 恰好 30 帧，时间轴完全对齐。
        Time.captureFramerate = _fps;
        Time.timeScale = 1f;

        yield return new WaitForSeconds(3f);

        // ---------- 坦克 ----------
        if (_plan == Plan.All || _plan == Plan.TankOnly)
        {
            var tank = Pick("land_main");
            if (tank != null)
            {
                var enemy = NearestEnemy(tank);
                StageTargetAhead(tank, enemy, 110f);
                Debug.Log("[Shot] 坦克段开始（目标已摆到正前方 110m）");
                yield return StartCoroutine(FollowAndFire(tank, 7f, 17f, 7f, 50f, 0.85f, 0f));
                yield return StartCoroutine(FollowAndFire(tank, 4f, 26f, 10f, 70f, 0.7f, 8f));
            }
        }

        // ---------- 飞机 ----------
        if (_plan == Plan.All || _plan == Plan.AirOnly)
        {
            var air = Pick("air_fighter");
            if (air != null)
            {
                Debug.Log("[Shot] 飞机段开始");
                // 飞机段：相机贴得更近、前视更远，避免只拍到山
                yield return StartCoroutine(FollowAndFire(air, 7f, 16f, 4f, 90f, 0.5f, 0f));
                yield return StartCoroutine(FollowAndFire(air, 4f, 24f, 6f, 120f, 0.5f, 10f));
            }
        }

        // ---------- 炮艇 ----------
        if (_plan == Plan.All || _plan == Plan.SeaOnly)
        {
            var ship = Pick("sea_gunboat");
            if (ship != null)
            {
                var enemy = NearestEnemy(ship);
                StageTargetAhead(ship, enemy, 140f);
                Debug.Log("[Shot] 炮艇段开始");
                yield return StartCoroutine(FollowAndFire(ship, 7f, 26f, 8f, 60f, 0.9f, 0f));
                yield return StartCoroutine(FollowAndFire(ship, 4f, 34f, 11f, 90f, 0.7f, 12f));
            }
        }

        RenderSettings.fog = fogWas;
        RestoreHUD();
        Time.captureFramerate = 0;      // 还原，别影响后续正常游玩

        _cam.targetTexture = null;
        _rt.Release();
        Destroy(_rt); Destroy(_tex); Destroy(_cam.gameObject);

        _running = false; _done = true;
        Debug.Log("[Shot] 完成 " + _frameNo + " 帧 → " + OutDir);
    }

    void OnDestroy() { RestoreHUD(); }
}
