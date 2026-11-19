using UnityEngine;
using System.IO;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 运行时"电影模式"：隐藏所有 HUD，用一台临时相机拍垂直抬升，逐帧存 PNG。
///
/// 为什么要运行时做：载具、AI、爆炸、弹道全是运行时生成的，编辑器里渲染出来是个空场景。
/// 为什么要临时相机：游戏主相机带着 HUD 的 OnGUI 绘制，直接用会拍到准星/血条；
/// 临时相机 + 把 HUD 组件 enabled=false，画面就"啥也没有"。
///
/// 用法（MCP script-execute 或 Play 后手动调用）：
///   CinematicCapture.Start(450, 30);   // 450 帧 @30fps = 15 秒
/// </summary>
public class CinematicCapture : MonoBehaviour
{
    public static CinematicCapture Instance;

    public static string OutDir = "D:/_tmp_video_test/crane_rt";

    int _total = 450;
    int _fps = 30;
    int _frame;
    Camera _cam;
    RenderTexture _rt;
    Texture2D _tex;
    bool _running;
    bool _done;

    readonly List<Behaviour> _hidden = new List<Behaviour>();
    Vector3 _baseXZ;

    // 拍摄前保存、拍完还原的全局渲染状态
    bool _fogWas;
    Color _fogColor;
    float _fogStart, _fogEnd, _fogDensity;
    FogMode _fogMode;

    public bool IsRunning => _running;
    public bool IsDone => _done;
    public int Frame => _frame;

    /// <summary>开始录制。frames = 总帧数，fps 只用于日志。</summary>
    public static CinematicCapture Start(int frames = 450, int fps = 30)
    {
        if (Instance != null && Instance._running)
        {
            Debug.LogWarning("[Cine] 已经在录了");
            return Instance;
        }
        var go = new GameObject("__CinematicCapture");
        var c = go.AddComponent<CinematicCapture>();
        c._total = frames;
        c._fps = fps;
        return c;
    }

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        StartCoroutine(Run());
    }

    void HideHUD()
    {
        // 把所有 OnGUI 的界面组件关掉（保留 MatchManager 逻辑继续跑）
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null) continue;
            string n = mb.GetType().Name;
            if (n == "MatchHUD" || n == "LockOnSystem" || n == "PauseScreen"
                || n == "ShopScreen" || n == "GarageScreen" || n == "PerfProbe")
            {
                if (mb.enabled)
                {
                    mb.enabled = false;
                    _hidden.Add(mb);
                }
            }
        }
        Debug.Log("[Cine] 已隐藏 HUD 组件 " + _hidden.Count + " 个");
    }

    void RestoreHUD()
    {
        foreach (var b in _hidden) if (b != null) b.enabled = true;
        _hidden.Clear();
    }

    IEnumerator Run()
    {
        _running = true;
        _done = false;
        Directory.CreateDirectory(OutDir);
        foreach (var f in Directory.GetFiles(OutDir, "*.png")) File.Delete(f);

        HideHUD();

        // 关雾：场景的线性雾 start=620 end=3000，相机升到 560 附近就会整片糊成灰蓝。
        // 电影镜头要的是"看得清战场"，所以拍摄期间关掉，拍完还原。
        _fogWas = RenderSettings.fog;
        _fogColor = RenderSettings.fogColor;
        _fogStart = RenderSettings.fogStartDistance;
        _fogEnd = RenderSettings.fogEndDistance;
        _fogDensity = RenderSettings.fogDensity;
        _fogMode = RenderSettings.fogMode;
        RenderSettings.fog = false;
        RenderSettings.fogStartDistance = 4000f;
        RenderSettings.fogEndDistance = 8000f;

        const int W = 1920, H = 1080;
        _rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
        _rt.antiAliasing = 4;
        _rt.Create();
        _tex = new Texture2D(W, H, TextureFormat.RGB24, false);

        var camGo = new GameObject("__CineCam");
        _cam = camGo.AddComponent<Camera>();
        _cam.clearFlags = CameraClearFlags.Skybox;
        _cam.fieldOfView = 55f;
        _cam.nearClipPlane = 0.5f;
        _cam.farClipPlane = 5000f;
        _cam.targetTexture = _rt;
        _cam.depth = 100;      // 盖住主相机

        // 战场中心偏南一点，抬升时视野扫过城区与海面
        _baseXZ = new Vector3(0f, 0f, -220f);

        const float StartY = 40f;
        const float EndY = 560f;
        // ⚠️ Unity 的 X 轴欧拉角：**正值 = 向下低头**，负值 = 向上抬头。
        // 第一版写成 PitchEnd = -68，相机越升越朝天，拍出来整屏是天空。
        // 现在：起始 4° 微微低头看远方，结束 72° 大俯角俯瞰全场。
        const float PitchStart = 4f;
        const float PitchEnd = 72f;

        for (_frame = 0; _frame < _total; _frame++)
        {
            float t = _frame / (float)(_total - 1);
            float e = Mathf.SmoothStep(0f, 1f, t);

            float y = Mathf.Lerp(StartY, EndY, e);
            float pitch = Mathf.Lerp(PitchStart, PitchEnd, e);

            _cam.transform.position = new Vector3(_baseXZ.x, y, _baseXZ.z);
            _cam.transform.rotation = Quaternion.Euler(pitch, 0f, 0f);

            // 等这一帧渲染完（EndOfFrame 之后再读，保证拿到当前帧）
            yield return new WaitForEndOfFrame();

            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            _tex.Apply();
            RenderTexture.active = null;

            File.WriteAllBytes(Path.Combine(OutDir, "c" + _frame.ToString("D4") + ".png"),
                               _tex.EncodeToPNG());

            if (_frame % 60 == 0)
                Debug.Log($"[Cine] 帧 {_frame}/{_total}  y={y:F0}  pitch={pitch:F0}");
        }

        RestoreHUD();

        // 还原雾效
        RenderSettings.fog = _fogWas;
        RenderSettings.fogColor = _fogColor;
        RenderSettings.fogStartDistance = _fogStart;
        RenderSettings.fogEndDistance = _fogEnd;
        RenderSettings.fogDensity = _fogDensity;
        RenderSettings.fogMode = _fogMode;

        _cam.targetTexture = null;
        _rt.Release();
        Object.Destroy(_rt);
        Object.Destroy(_tex);
        Object.Destroy(_cam.gameObject);

        _running = false;
        _done = true;
        Debug.Log($"[Cine] 完成 {_total} 帧 → {OutDir}");
    }

    void OnDestroy()
    {
        RestoreHUD();
    }
}
