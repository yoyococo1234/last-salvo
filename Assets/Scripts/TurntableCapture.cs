using UnityEngine;
using System.IO;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 载具转台渲染（演示视频用）：把 6 种载具**同框排开**，相机缓慢环绕/横移，一镜到底。
///
/// 2026-11-19 修两处（用户反馈）：
///   ① **颜色和游戏里不一样** —— 第一版为了做黑底，把 RenderSettings.skybox 置空、
///      ambientMode 改成 Flat 蓝灰、还额外加了一盏 1.5 强度方向光。
///      结果模型受光完全变样（偏青蓝）。现在**光照环境一点不动**，
///      只把**相机背景**设成纯色（背景是 Camera 自己的属性，不影响模型受光）。
///   ② **6 台要同框** —— 第一版是 6 台依次各转 100 帧（一段一个）。
///      现在排成一排，相机一镜横扫，6 台全在画面里。
///
/// 用法：TurntableCapture.Start(600);
/// </summary>
public class TurntableCapture : MonoBehaviour
{
    public static TurntableCapture Instance;
    public static string OutDir = "D:/_tmp_video_test/turntable";

    int _totalFrames = 600;
    bool _running, _done;
    Camera _cam;
    RenderTexture _rt;
    Texture2D _tex;
    readonly List<Behaviour> _hidden = new List<Behaviour>();
    readonly List<GameObject> _stage = new List<GameObject>();
    int _frameNo;

    public bool IsRunning => _running;
    public bool IsDone => _done;
    public int FrameNo => _frameNo;

    public static TurntableCapture Start(int totalFrames = 600)
    {
        if (Instance != null && Instance._running) return Instance;
        var go = new GameObject("__TurntableCapture");
        var c = go.AddComponent<TurntableCapture>();
        c._totalFrames = totalFrames;
        return c;
    }

    void Awake() { Instance = this; }
    void Start() { StartCoroutine(Run()); }

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

    /// <summary>克隆一份载具，剥掉逻辑脚本，只留可渲染的模型。</summary>
    GameObject MakeDisplayPiece(VehicleCombat src)
    {
        var clone = Instantiate(src.gameObject);
        clone.name = "__tt_" + src.Def.id;
        clone.transform.rotation = Quaternion.identity;

        foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null) Destroy(mb);
        foreach (var c in clone.GetComponentsInChildren<Collider>(true))
            if (c != null) Destroy(c);
        foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
            if (rb != null) Destroy(rb);

        return clone;
    }

    /// <summary>取一个对象的渲染包围盒（世界空间）。</summary>
    Bounds WorldBounds(GameObject go)
    {
        bool has = false;
        Bounds b = new Bounds(go.transform.position, Vector3.one);
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            if (r == null) continue;
            if (!has) { b = r.bounds; has = true; }
            else b.Encapsulate(r.bounds);
        }
        return b;
    }

    IEnumerator Run()
    {
        _running = true; _done = false;
        _frameNo = 0;
        Directory.CreateDirectory(OutDir);
        foreach (var f in Directory.GetFiles(OutDir, "*.png")) File.Delete(f);

        HideHUD();

        // ⚠️ 光照环境一律不动：天空盒、ambient、雾、场景 Sun 全部保持游戏原样，
        //    这样模型颜色和游戏里完全一致。只有相机背景是我们自己设的。
        const int W = 1920, H = 1080;
        _rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
        _rt.antiAliasing = 4;
        _rt.Create();
        _tex = new Texture2D(W, H, TextureFormat.RGB24, false);

        var camGo = new GameObject("__TT_Cam");
        _cam = camGo.AddComponent<Camera>();
        _cam.clearFlags = CameraClearFlags.SolidColor;      // 背景纯色，模型照常用天空盒环境光
        _cam.backgroundColor = new Color(0.02f, 0.025f, 0.035f);
        _cam.fieldOfView = 42f;
        _cam.nearClipPlane = 0.3f;
        _cam.farClipPlane = 800f;
        _cam.targetTexture = _rt;
        _cam.depth = 200;

        // 收集 6 种载具（每种取一台），按固定顺序
        var order = new[] { "land_light", "land_main", "land_heavy", "sea_gunboat", "air_fighter", "air_attacker" };
        var picks = new List<VehicleCombat>();
        var seen = new HashSet<string>();
        foreach (var v in Object.FindObjectsByType<VehicleCombat>(FindObjectsSortMode.None))
        {
            if (v == null || v.Def == null || seen.Contains(v.Def.id)) continue;
            seen.Add(v.Def.id);
            picks.Add(v);
        }
        picks.Sort((a, b) => System.Array.IndexOf(order, a.Def.id).CompareTo(System.Array.IndexOf(order, b.Def.id)));

        Debug.Log("[TT] 载具类型 " + picks.Count + " 种");

        // ---- 摆成 2 行 × 3 列，每台**各自自转**（用户要求）----
        var pieces = new List<GameObject>();
        var sizes = new List<Vector2>();      // 各自的 (宽, 深)

        foreach (var src in picks)
        {
            var p = MakeDisplayPiece(src);
            yield return null;                       // 等一帧让包围盒算准
            var b = WorldBounds(p);
            sizes.Add(new Vector2(Mathf.Max(1f, b.size.x), Mathf.Max(1f, b.size.z)));
            pieces.Add(p);
        }

        // 每台统一缩放到"最大边 = 9 m"，保证 6 台视觉体量接近
        const float targetSize = 16f;
        float maxExtent = 1f;
        foreach (var s in sizes) maxExtent = Mathf.Max(maxExtent, Mathf.Max(s.x, s.y));
        float uniformScale = targetSize / maxExtent;

        const int Cols = 3, Rows = 2;
        const float gapX = 2f, gapZ = 3f;
        float cellW = targetSize * 1.15f + gapX;
        float cellD = targetSize * 1.15f + gapZ;

        // ⚠️ 舞台必须搬到"什么都没有"的地方。
        // 第一版放在地图原点 (0,0,0) —— 那里正是城区中心，镜头里全是建筑和草地。
        // 现在整体抬到 3000 m 高空：地面在 2980 m 外（farClip=800 根本看不到），周围没有任何物件。
        const float StageY = 3000f;

        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            p.transform.localScale = Vector3.one * uniformScale;

            int col = i % Cols;
            int row = i / Cols;
            float x = (col - (Cols - 1) * 0.5f) * cellW;
            float z = (row - (Rows - 1) * 0.5f) * cellD;

            // 落地：包围盒底边贴到舞台平面
            p.transform.position = new Vector3(x, 0f, z);
            yield return null;
            var b = WorldBounds(p);
            p.transform.position = new Vector3(x, StageY - b.min.y, z);

            _stage.Add(p);
        }

        // 相机：正对 2×3 阵列，稍微俯视，全收
        // 2026-11-19 调近：第一版距离 = gridW*0.95+20 ≈ 90 m，模型在 1080p 里只占 ~60 px，太小。
        // 现在按"整排宽度恰好填满画面 92%"反推距离（水平 FOV 由竖直 FOV 和 16:9 决定）。
        float gridW = Cols * cellW;
        float gridD = Rows * cellD;

        float vFov = 34f;
        _cam.fieldOfView = vFov;
        float hFov = 2f * Mathf.Atan(Mathf.Tan(vFov * 0.5f * Mathf.Deg2Rad) * 16f / 9f) * Mathf.Rad2Deg;
        // 0.80 → 0.95：整排占画面 95% 宽（几乎贴边），模型尽量大
        float camDist = (gridW * 0.5f) / Mathf.Tan(hFov * 0.5f * Mathf.Deg2Rad) / 0.95f;
        // 2026-11-19 用户："得往上稍微调一点视角，意思是稍微往上抬一抬"
        // 0.14 → 0.34：相机抬到更高处，俯视角更大，能看清 2 行阵列的纵深（不再压成一条线）
        float camHeight = camDist * 0.34f;
        Vector3 center = new Vector3(0f, StageY + 3.2f, 0f);

        Debug.Log("[TT] 阵列 " + gridW.ToString("F0") + "×" + gridD.ToString("F0")
                + " m，缩放 " + uniformScale.ToString("F2")
                + "，相机距离 " + camDist.ToString("F0"));

        for (int i = 0; i < _totalFrames; i++)
        {
            float t = i / (float)Mathf.Max(1, _totalFrames - 1);
            float e = Mathf.SmoothStep(0f, 1f, t);

            // 6 台同时自转：一整圈 360°，缓入缓出
            float spin = e * 360f;
            foreach (var p in pieces)
                if (p != null) p.transform.rotation = Quaternion.Euler(0f, spin, 0f);

            // 相机极缓慢地左右摆一点（±8°），增加镜头呼吸感但不破坏"都在转"的观感
            float yaw = Mathf.Lerp(-8f, 8f, e);
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 offset = rot * new Vector3(0f, 0f, -camDist);
            _cam.transform.position = center + offset + Vector3.up * camHeight;
            _cam.transform.LookAt(center);

            yield return new WaitForEndOfFrame();

            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            _tex.Apply();
            RenderTexture.active = null;

            File.WriteAllBytes(Path.Combine(OutDir, "t" + _frameNo.ToString("D4") + ".png"),
                               _tex.EncodeToPNG());
            _frameNo++;

            if (_frameNo % 100 == 0) Debug.Log("[TT] 帧 " + _frameNo + "/" + _totalFrames);
        }

        RestoreHUD();

        _cam.targetTexture = null;
        _rt.Release();
        Destroy(_rt); Destroy(_tex); Destroy(_cam.gameObject);
        foreach (var g in _stage) if (g != null) Destroy(g);
        _stage.Clear();

        _running = false; _done = true;
        Debug.Log("[TT] 完成 " + _frameNo + " 帧 → " + OutDir);
    }

    void OnDestroy() { RestoreHUD(); }
}
