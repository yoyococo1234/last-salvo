using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using System.IO;

/// <summary>
/// 命令行打包入口（2026-11-12 加）。
///
/// 为什么需要它：MCP 编辑器插件在长时间会话里会反复掉线，`script-execute` 调不动。
/// 这条路不依赖编辑器连接 —— 关掉 GUI 编辑器后直接：
///
///   set TANK3D_BUILD_DIR=D:\Builds\LastSalvo_Win_20261118k
///   Unity.exe -batchmode -quit -projectPath D:\UnityProjects\Tank3D ^
///             -executeMethod BuildCli.BuildWindows -logFile D:\Builds\build.log
///
/// 输出目录从环境变量 `TANK3D_BUILD_DIR` 取，没设就用 GameBuilder 的默认目录。
/// </summary>
public static class BuildCli
{
    const string Scene = "Assets/Scenes/Map_Battlefield.unity";

    // 2026-11-18 改名：铁皮黎明 → 最后校射 / Last Salvo
    const string ExeName = "LastSalvo";
    const string DefaultDir = "D:/Builds/LastSalvo_Win";

    public static void BuildWindows()
    {
        string outDir = System.Environment.GetEnvironmentVariable("TANK3D_BUILD_DIR");
        if (string.IsNullOrEmpty(outDir)) outDir = DefaultDir;
        Directory.CreateDirectory(outDir);

        // 先打印一份"海平面 / 水域"体检，方便确认抬升效果（读日志即可）
        LogWaterStats();

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = outDir + "/" + ExeName + ".exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(opts);
        var s = report.summary;
        string msg = $"[BuildCli] {s.result} 大小={s.totalSize / 1048576f:F0}MB 用时={s.totalTime.TotalSeconds:F0}s 错误={s.totalErrors} 输出={outDir}";
        Debug.Log(msg);

        // batchmode 的退出码：失败时返回非 0，方便外面判断
        if (Application.isBatchMode)
            EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }

    /// <summary>
    /// WebGL 命令行打包（2026-11-19 加，给黑客松提交用）。
    ///
    ///   set TANK3D_BUILD_DIR=D:\Builds\LastSalvo_WebGL
    ///   Unity.exe -batchmode -quit -projectPath D:\UnityProjects\Tank3D ^
    ///             -executeMethod BuildCli.BuildWebGL -logFile D:\Builds\webgl.log
    ///
    /// 注意：WebGL 在编辑器里跑不了 Play，只能出包后拿浏览器验。
    /// 这个工程的体积大头是地形（4 张 75.7 MB 的 TerrainData）+ 模型，
    /// 首包会比较大 —— 日志里会打印实际大小，超过 200 MB 就得考虑压资源。
    /// </summary>
    public static void BuildWebGL()
    {
        string outDir = System.Environment.GetEnvironmentVariable("TANK3D_BUILD_DIR");
        if (string.IsNullOrEmpty(outDir)) outDir = "D:/Builds/LastSalvo_WebGL";
        Directory.CreateDirectory(outDir);

        // 压缩优先：WebGL 的 Brotli 能显著压小首包，但需要服务器发对 Content-Encoding。
        // 先不强行设，保持项目里的 PlayerSettings 原样，只在日志里报出来。
        //
        // 2026-11-19 实测坑：**Brotli 在这个工程上会卡死**。
        // 现象：构建走到压缩阶段后 brotli.exe 挂住不退出、一直占着
        // `Library/Bee/artifacts/WebGL/webgl.data` 的句柄，主进程 CPU 归零、日志停住；
        // 下一次构建再跑就报 `IOException: ... being used by another process`。
        // 已经两次踩到（第一次卡 15 分钟，第二次 142 秒直接失败）。
        // 因此这里允许用环境变量 `TANK3D_WEBGL_GZIP=1` 切到 Gzip 绕开（体积多 ~10%）。
        string gz = System.Environment.GetEnvironmentVariable("TANK3D_WEBGL_GZIP");
        if (!string.IsNullOrEmpty(gz) && gz != "0")
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            Debug.Log("[BuildCli] 已切换压缩格式为 Gzip（TANK3D_WEBGL_GZIP=1，规避 Brotli 卡死）");
        }
        else if (PlayerSettings.WebGL.compressionFormat == WebGLCompressionFormat.Brotli)
        {
            Debug.LogWarning("[BuildCli] 当前是 Brotli —— 本工程实测会卡死！建议加 TANK3D_WEBGL_GZIP=1");
        }

        Debug.Log($"[BuildCli] WebGL 输出目录 = {outDir}");
        Debug.Log($"[BuildCli] 压缩格式 = {PlayerSettings.WebGL.compressionFormat}  " +
                  $"dataCaching = {PlayerSettings.WebGL.dataCaching}  " +
                  $"内存 = {PlayerSettings.WebGL.memorySize} MB  " +
                  $"异常 = {PlayerSettings.WebGL.exceptionSupport}");

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = outDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(opts);
        var s = report.summary;
        Debug.Log($"[BuildCli] WebGL {s.result} 大小={s.totalSize / 1048576f:F1}MB " +
                  $"用时={s.totalTime.TotalSeconds:F0}s 错误={s.totalErrors} 输出={outDir}");

        // 把每个产出文件的大小也列出来，方便判断首包多大
        if (Directory.Exists(outDir))
        {
            var files = new DirectoryInfo(outDir).GetFiles("*", SearchOption.AllDirectories);
            System.Array.Sort(files, (a, b) => b.Length.CompareTo(a.Length));
            int n = 0;
            foreach (var f in files)
            {
                Debug.Log($"[BuildCli]   {f.Length / 1048576f:F1} MB  {f.FullName.Substring(outDir.Length + 1)}");
                if (++n >= 12) break;
            }
        }

        if (Application.isBatchMode)
            EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }

    /// <summary>海平面体检：水域占比、海岸剖面、船出生深度（只读，写日志）。</summary>
    public static void LogWaterStats()
    {
        // batchmode 下要自己把场景打开
        var cur = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(cur.path))
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(Scene);

        float wl = BattlefieldTerrain.WaterLevel;
        int N = 120, wet = 0;
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                float x = -1000f + (i + 0.5f) * (2000f / N);
                float z = -1000f + (j + 0.5f) * (2000f / N);
                if (BattlefieldTerrain.SampleHeight(x, z) < wl) wet++;
            }
        Debug.Log($"[Water] 海平面={wl:F2}  水域占比={wet / (float)(N * N) * 100f:F1}%");

        // 港口附近：水面到岸线多远、岸上目标比海平面高多少
        float shoreX = -1000f;
        for (float x = -1000f; x <= -200f; x += 5f)
            if (BattlefieldTerrain.SampleHeight(x, 280f) >= wl) { shoreX = x; break; }
        float inlandH = BattlefieldTerrain.SampleHeight(shoreX + 120f, 280f);
        Debug.Log($"[Water] 港口岸线 x≈{shoreX:F0}（z=280）；岸内 120 m 处地形高度={inlandH:F2}（比海平面高 {inlandH - wl:F1} m）");

        // 船出生点：沿 z=280 找第一个够 3 m 深的点
        for (float x = -1000f; x <= 0f; x += 10f)
        {
            float d = BattlefieldTerrain.WaterDepth(x, 280f);
            if (d >= 3f) { Debug.Log($"[Water] 首个 ≥3 m 深水域 x≈{x:F0}（距岸线约 {shoreX - x:F0} m）"); break; }
        }
    }
}
