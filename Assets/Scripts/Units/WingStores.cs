using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 机翼挂架上的外挂物（导弹）。打一发少一枚，复活时重新挂满。
///
/// ================== 2026-11-17 15:30 重写（第三版） ==================
///
/// 用户三次反馈："战斗机导弹和机身差一点点 / 攻击机仍然差很多 / 我要那种几乎是挂载的感觉"。
///
/// ## 前两版为什么都不对
///
/// v1（写死米数常数，如 `bestZ - 0.18f`）：两个机型网格空间差 100 倍，常数在战斗机上放大成 18 米。
/// v2（在根节点局部空间摆弹，尺寸按"世界最长边 × 比例"）：**方向对了，但 `downOffset`
///    只是"弹径 × 1.35"，跟机翼下表面的真实高度无关** —— 弹要么陷进机翼、要么悬空。
///    而且用 `max(size.x, size.y, size.z)` 当翼展：战斗机的机长(10.57) > 翼展(8.96)，
///    取成了机长，所有比例全部偏大。
///
/// ## v3 的做法：**从网格顶点实测机翼下表面，弹顶精确贴上去**
///
/// 1. 把模型所有顶点换算到**飞机根节点局部空间**（x=右 y=上 z=前），只做这一次变换。
/// 2. 自动找出**机翼的 z 带**（哪个前后区间里横向跨度 > 75% 全翼展）。
/// 3. 在机翼 z 带内，按 |x| 分成 48 个桶，每桶取**最低 y** = 该处的机翼下表面高度。
/// 4. 挂弹时查表：`弹顶 y = 该处机翼下表面 y - 挂架高`，挂架从机翼一直连到弹顶。
///
/// **不写死任何米数**，两个机型（网格 17.6×缩放 0.6 / 网格 0.09×缩放 100）自动适配。
///
/// ## 实测数据（MCP 在编辑器里跑出来的，root 局部空间，单位=米）
///
///   攻击机 attacker_nomissile：bbox 10.56 × 3.35 × 10.52，翼展 10.56，机翼 z 带 [-1.05, 1.05]
///     机翼下表面：t=0.44 → -0.42    t=0.54 → -0.32    t=0.64 → -0.21    t=0.74 → -0.11
///     （t < 0.38 是发动机短舱/主起落架，不能当机翼用）
///     机腹最低 y = -0.687
///
///   战斗机 air_fighter_v2：bbox 8.96 × 4.54 × 10.57，翼展 8.96，机翼 z 带 [-2.86, -0.48]（三角翼）
///     机翼下表面几乎水平：t=0.40~0.92 都是 1.42~1.47
///     机腹最低 y = 0.305
///
/// 注意：两个机型的**机长都大于翼展**，所以翼展必须取 `size.x`（飞机自身轴系的左右方向），
/// 不能取三轴最大值。
/// </summary>
public class WingStores : MonoBehaviour
{
    public readonly List<GameObject> all = new List<GameObject>();

    [Tooltip("挂弹用的模型节点（由 VehicleModelBinder 填）")]
    public Transform model;
    [Tooltip("每侧挂几枚（total = 0 时才生效）")]
    public int perSide = 4;
    [Tooltip("总共挂几枚（0 = 用 perSide×2）。设成实际备弹数，让机翼上的弹和能打出的发数一致")]
    public int total = 0;
    public Material mat;

    [Tooltip("缓存键（机型 id），由 VehicleModelBinder 填。避免每架飞机都重新扫一遍网格")]
    public string profileKey;

    int _fired;
    bool _built;

    /// <summary>还剩几枚（视觉）。</summary>
    public int Remaining => Mathf.Max(0, all.Count - _fired);

    void Start()
    {
        if (!_built && model != null) BuildProps();
    }

    // ==================================================================
    //  机翼几何档案（root 局部空间，单位=米）
    // ==================================================================
    class WingProfile
    {
        public bool valid;
        public float halfSpan;       // 半翼展（|x| 最大值）
        public float wingZ0, wingZ1; // 机翼前后带
        public float wingZMid;       // 机翼带中心（弹体 z 位置）
        public float bellyY;         // 机腹最低 y（单发走机腹时用）
        public float minY;           // 全机最低 y（起落架）
        public int bins;
        public float[] binY;         // 每个 |x| 桶的机翼下表面 y
        public float[] binYmax;      // 每个 |x| 桶的机翼上表面 y（量厚度用）

        // 原始顶点（root 局部空间），用于在**具体挂点处**做局部二维采样。
        // 为什么不能只用一维 |x| 桶：桶里取的是整条翼弦范围内的极值，
        // 机翼上方的**发动机短舱**会把 binYmax 顶到很高，算出来的"翼厚"是假的，
        // 挂架按假翼厚埋进去就会从机翼上表面穿出来（2026-11-17 实测）。
        public Vector3[] rawPts;

        /// <summary>
        /// 在 (x, z) 附近的小窗口内采样机翼的**局部**上下表面。
        /// window 是窗口半宽（米）。
        /// </summary>
        public void LocalSurface(float x, float z, float window, out float yBot, out float yTop, out int hits)
        {
            yBot = float.MaxValue; yTop = float.MinValue; hits = 0;
            if (rawPts == null) return;
            for (int i = 0; i < rawPts.Length; i++)
            {
                var q = rawPts[i];
                if (Mathf.Abs(q.x - x) > window) continue;
                if (Mathf.Abs(q.z - z) > window) continue;
                if (q.y < yBot) yBot = q.y;
                if (q.y > yTop) yTop = q.y;
                hits++;
            }
        }

        /// <summary>给 |x| 归一化位置 t，查该处机翼**上**表面 y（用来限制挂架不要穿出翼面）。</summary>
        public float WingTopAt(float t)
        {
            if (binYmax == null) return 0f;
            int bi = Mathf.Clamp(Mathf.RoundToInt(t * (bins - 1)), 0, bins - 1);
            for (int d = 0; d < bins; d++)
            {
                int a = bi - d, b = bi + d;
                if (a >= 0 && binYmax[a] < 1e6f) return binYmax[a];
                if (b < bins && binYmax[b] < 1e6f) return binYmax[b];
            }
            return 0f;
        }

        /// <summary>给 |x| 归一化位置 t（0=机身中线，1=翼尖），查该处机翼下表面 y。</summary>
        public float WingUnderside(float t)
        {
            if (binY == null) return 0f;
            int bi = Mathf.Clamp(Mathf.RoundToInt(t * (bins - 1)), 0, bins - 1);
            for (int d = 0; d < bins; d++)
            {
                int a = bi - d, b = bi + d;
                if (a >= 0 && binY[a] > -1e6f) return binY[a];
                if (b < bins && binY[b] > -1e6f) return binY[b];
            }
            return 0f;
        }
    }

    static readonly Dictionary<string, WingProfile> _cache = new Dictionary<string, WingProfile>();

    /// <summary>清空机翼采样缓存（改了模型/缩放后调用；域重载时也会自动清空）。</summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>
    /// 扫一遍模型网格，量出机翼数据。结果按机型缓存（同一个机型的多架飞机共用）。
    /// </summary>
    static WingProfile Measure(Transform root, Transform model, string key)
    {
        if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var hit)) return hit;

        var pf = new WingProfile();
        if (root == null || model == null) return pf;

        // ---------- 1. 顶点 → 飞机根节点局部空间 ----------
        var pts = new List<Vector3>(200000);
        Matrix4x4 toRoot = root.worldToLocalMatrix;
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
        {
            var me = mf.sharedMesh;
            if (me == null || !me.isReadable) continue;
            Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
            var vs = me.vertices;
            for (int i = 0; i < vs.Length; i++) pts.Add(m.MultiplyPoint3x4(vs[i]));
        }
        if (pts.Count < 64) return pf;

        // ---------- 2. 包围盒 ----------
        Vector3 mn = pts[0], mx = pts[0];
        for (int i = 1; i < pts.Count; i++) { mn = Vector3.Min(mn, pts[i]); mx = Vector3.Max(mx, pts[i]); }
        float span = mx.x - mn.x;
        if (span < 0.01f) return pf;
        float cx = (mn.x + mx.x) * 0.5f;
        float cz = (mn.z + mx.z) * 0.5f;
        float lenZ = mx.z - mn.z;

        pf.halfSpan = span * 0.5f;
        pf.minY = mn.y;

        // ---------- 3. 找机翼的 z 带：哪个前后区间里横向跨度 > 75% 全翼展 ----------
        const int NZ = 48;
        float wz0 = float.MaxValue, wz1 = float.MinValue;
        for (int i = 0; i < NZ; i++)
        {
            float z0 = mn.z + lenZ * i / NZ;
            float z1 = mn.z + lenZ * (i + 1) / NZ;
            float a = float.MaxValue, b = float.MinValue;
            for (int k = 0; k < pts.Count; k++)
            {
                var q = pts[k];
                if (q.z < z0 || q.z >= z1) continue;
                if (q.x < a) a = q.x;
                if (q.x > b) b = q.x;
            }
            if (a > b) continue;
            if ((b - a) / span > 0.75f)
            {
                if (z0 < wz0) wz0 = z0;
                if (z1 > wz1) wz1 = z1;
            }
        }
        if (wz0 >= wz1) { wz0 = cz - lenZ * 0.15f; wz1 = cz + lenZ * 0.15f; }   // 兜底
        pf.wingZ0 = wz0;
        pf.wingZ1 = wz1;
        pf.wingZMid = (wz0 + wz1) * 0.5f;

        // ---------- 4. 机翼 z 带内，按 |x| 分桶取最低/最高 y ----------
        const int NB = 48;
        pf.bins = NB;
        pf.binY = new float[NB];
        pf.binYmax = new float[NB];
        for (int i = 0; i < NB; i++) { pf.binY[i] = 1e6f; pf.binYmax[i] = -1e6f; }

        float bellyMin = 1e6f;
        for (int k = 0; k < pts.Count; k++)
        {
            var q = pts[k];
            if (q.z < wz0 || q.z > wz1) continue;
            float ax = Mathf.Abs(q.x - cx);
            int bi = Mathf.Clamp((int)(ax / pf.halfSpan * NB), 0, NB - 1);
            if (q.y < pf.binY[bi]) pf.binY[bi] = q.y;
            if (q.y > pf.binYmax[bi]) pf.binYmax[bi] = q.y;
            // 机腹：横向 10% 以内
            if (ax < pf.halfSpan * 0.10f && q.y < bellyMin) bellyMin = q.y;
        }
        pf.bellyY = bellyMin < 1e5f ? bellyMin : mn.y;

        // 空桶填成 ±1e6 的哨兵，查询时会向邻居插值
        for (int i = 0; i < NB; i++)
        {
            if (pf.binY[i] > 1e5f) pf.binY[i] = -1e6f;
            if (pf.binYmax[i] < -1e5f) pf.binYmax[i] = 1e6f;
        }

        pf.rawPts = pts.ToArray();
        pf.valid = true;
        if (!string.IsNullOrEmpty(key)) _cache[key] = pf;

        Debug.Log(string.Format(
            "[WingStores] {0} 采样完成：翼展={1:F2}m 机翼z带=[{2:F2},{3:F2}] 机腹y={4:F2} 全机最低y={5:F2} 顶点={6}",
            string.IsNullOrEmpty(key) ? model.name : key, span, wz0, wz1, pf.bellyY, mn.y, pts.Count));
        return pf;
    }

    // ==================================================================
    //  建弹
    // ==================================================================
    public void BuildProps()
    {
        _built = true;
        if (model == null) return;

        var pf = Measure(transform, model, profileKey);
        if (!pf.valid)
        {
            Debug.LogWarning("[WingStores] 机翼采样失败，跳过挂弹（model=" + model.name + "）");
            return;
        }

        var useMat = mat != null ? mat : MakeMat();

        // ---------- 弹体尺寸（世界米 → root 局部单位） ----------
        // root 一般是 scale=1 的载具节点，但按 lossyScale 换算，防止有外层缩放。
        Vector3 rs = transform.lossyScale;
        float s = Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(rs.x), Mathf.Max(Mathf.Abs(rs.y), Mathf.Abs(rs.z))));

        // 弹长按**翼展**比例取（不是三轴最大边）。实测：攻击机翼展 10.56、战斗机 8.96。
        // 真实挂载比例：AGM-65 长 2.5m / A-10 翼展 17.5m ≈ 0.14；AIM-9 长 3.0m / 翼展 9m ≈ 0.33。
        // 取 0.20，两端都像真弹，而且不会长到超出机翼弦长（机翼弦长只有 2.1~2.4 m）。
        float spanWorld = pf.halfSpan * 2f * s;

        int count = total > 0 ? total : perSide * 2;
        if (count < 1) count = 1;

        float lenWorld = Mathf.Clamp(spanWorld * 0.20f, 0.9f, 2.8f);
        float diaWorld = lenWorld * 0.115f;
        // 单发（战斗机机腹）用**更短的弹**：实测战斗机翼展 8.96 m，按 0.20 算出 1.79 m，
        // 但它的机腹离地间隙和起落架舱都很紧，1.79 m 的弹从机腹一直伸到机尾，
        // 视觉上像挂了一根管子（2026-11-17 实测）。机腹半埋挂架用更紧凑的比例，取 0.13 ≈ 1.17 m。
        if (count == 1)
        {
            lenWorld = Mathf.Clamp(spanWorld * 0.13f, 0.8f, 1.6f);
            diaWorld = lenWorld * 0.13f;
        }

        float L = lenWorld / s;
        float D = diaWorld / s;
        float pylonH = D * 1.7f;          // 挂架可见高度（弹顶到机翼下表面）

        // ---------- 单发：机腹中线 ----------
        if (count == 1)
        {
            // 挂点 z：机翼带**靠前**（0.25）。实测战斗机的机翼 z 带是 [-2.86, -0.48]（三角翼），
            // 0.45 落在 -1.79，正好在**主起落架舱**下面，导弹被起落架挡住看不见。
            // 往前挪到 0.25（≈ -2.27），让开起落架，从斜前方一眼就能看见弹。
            float pz = Mathf.Lerp(pf.wingZ0, pf.wingZ1, 0.25f);

            // 用**该 z 位置**的机身局部下表面，不是整条机翼带的最低值。
            // 上一版用 pf.bellyY（全机翼带最低点），战斗机算出 1.85 m 的超长挂架，
            // 视觉上像吊着一根棍子（2026-11-17 实测）。
            // 窗口必须**小**（0.16 m）。上一版用 halfSpan*0.10≈0.45 m，
            // 把机翼和垂尾都框进来了，"机身厚度"量成 1.83 m，
            // 挂架埋入 1.1 m，侧视像吊着一根棍子（2026-11-17 实测）。
            pf.LocalSurface(0f, pz, 0.16f, out float bellyBot, out float bellyTop, out int bHits);
            float bellyY = (bHits > 4 && bellyBot < 1e5f) ? bellyBot : pf.bellyY;

            float topY = bellyY - pylonH;
            float axisY = topY - D * 0.5f;

            // 埋入深度：机腹挂弹是"挂架贴腹"，只需埋进一点点把缝焊死。
            // 上一版按机身厚度一半算，战斗机埋了 1.5 m，侧视像吊着一根棍子。
            float bodyThk = D * 0.55f;
            var m0 = new GameObject("Missile_0");
            m0.transform.SetParent(transform, false);
            m0.transform.localPosition = new Vector3(0f, axisY, pz);
            m0.transform.localRotation = Quaternion.identity;
            BuildOne(m0.transform, L, D, pylonH, bodyThk, useMat);
            all.Add(m0);
        }
        else
        {
            int perSideCount = Mathf.Max(1, Mathf.CeilToInt(count / 2f));
            int idx = 0;

            for (int i = 0; i < count; i++)
            {
                int side = (i % 2 == 0) ? -1 : 1;
                int row = i / 2;

                // 挂点沿翼展的归一化位置 t（0=机身中线，1=翼尖）。
                // 起始 0.44：攻击机 t<0.40 是发动机短舱和主起落架，不能挂弹。
                // 终止 0.80：再往外机翼太薄，而且靠近翼尖的副翼。
                float t = perSideCount <= 1
                    ? 0.52f
                    : Mathf.Lerp(0.44f, 0.80f, row / (float)(perSideCount - 1));

                float px = side * pf.halfSpan * t;
                float pz = pf.wingZMid;

                // **在挂点实际位置**做二维局部采样（不是一维 |x| 桶）。
                // 一维桶会把机翼上方的发动机短舱算进"上表面"，翼厚虚高，
                // 挂架按假翼厚埋进去就会从机翼上表面穿出来（2026-11-17 实测过）。
                pf.LocalSurface(px, pz, pf.halfSpan * 0.05f, out float locBot, out float locTop, out int locHits);
                float wingY = (locHits > 8 && locBot < 1e5f) ? locBot : pf.WingUnderside(t);
                float wingTopY = (locHits > 8 && locTop > -1e5f) ? locTop : wingY + D * 1.2f;

                float topY = wingY - pylonH;
                float axisY = topY - D * 0.5f;

                // 挂架埋入深度：把"挂架顶"推到机翼**局部下表面与上表面之间**，
                // 留 25% 余量不穿出上表面。这样既没有缝（用户要的"几乎是挂载"），
                // 也不会像上一版那样从翼面上方冒出几个方块。
                float wingThk = Mathf.Max(D * 0.5f, wingTopY - wingY);
                float bury = wingThk * 0.75f;

                var m = new GameObject("Missile_" + idx);
                m.transform.SetParent(transform, false);
                m.transform.localPosition = new Vector3(px, axisY, pz);
                m.transform.localRotation = Quaternion.identity;
                BuildOne(m.transform, L, D, pylonH, bury, useMat);
                all.Add(m);
                idx++;
            }
        }

        Debug.Log(string.Format(
            "[WingStores] {0} 挂弹 {1} 枚：弹长={2:F2}m 弹径={3:F3}m 挂架高={4:F2}m（翼展 {5:F2}m）",
            profileKey, all.Count, lenWorld, diaWorld, pylonH / s * s, spanWorld));
    }

    /// <summary>在弹自身局部空间拼一枚导弹。弹原点在**弹体中心**，+Z 是弹头方向。</summary>
    static void BuildOne(Transform root, float L, float D, float pylonH, float pylonBury, Material mat)
    {
        // 弹体（圆柱，沿 +Z）
        Part(root, PrimitiveType.Cylinder, new Vector3(D, L * 0.5f, D),
             Vector3.zero, new Vector3(90f, 0f, 0f), mat);
        // 弹头（压扁球，前端收尖）
        Part(root, PrimitiveType.Sphere, new Vector3(D * 0.96f, D * 1.5f, D * 0.96f),
             new Vector3(0f, 0f, L * 0.50f), Vector3.zero, mat);
        // 尾喷口
        Part(root, PrimitiveType.Cylinder, new Vector3(D * 0.86f, D * 0.35f, D * 0.86f),
             new Vector3(0f, 0f, -L * 0.50f), new Vector3(90f, 0f, 0f), mat);

        // 尾翼（十字四片）
        float finZ = -L * 0.42f;
        float finLen = L * 0.30f;
        float finSpan = D * 1.9f;
        Part(root, PrimitiveType.Cube, new Vector3(finSpan, L * 0.014f, finLen),
             new Vector3(0f, 0f, finZ), Vector3.zero, mat);
        Part(root, PrimitiveType.Cube, new Vector3(L * 0.014f, finSpan, finLen),
             new Vector3(0f, 0f, finZ), Vector3.zero, mat);

        // 中部弹翼（两片，只左右，增加"导弹感"）
        float midZ = L * 0.06f;
        float midSpan = D * 1.5f;
        float midLen = L * 0.22f;
        Part(root, PrimitiveType.Cube, new Vector3(midSpan, L * 0.012f, midLen),
             new Vector3(0f, 0f, midZ), Vector3.zero, mat);

        // 挂架：从弹顶（y=+D/2）向上，**穿过机翼下表面埋进去**。
        // 造型仿真实挂架：窄立柱 + 下方小整流罩，不是一块方板（方板在侧视里非常假）。
        float pylBase = D * 0.5f;                 // 弹顶
        float pylTotal = pylonH + pylonBury;
        // 立柱：窄、前后收一点
        Part(root, PrimitiveType.Cube, new Vector3(D * 0.30f, pylTotal, L * 0.26f),
             new Vector3(0f, pylBase + pylTotal * 0.5f, L * 0.05f), Vector3.zero, mat);
        // 挂架底部整流罩（连接弹体的那一小段，比立柱略宽）
        Part(root, PrimitiveType.Cube, new Vector3(D * 0.52f, pylonH * 0.34f, L * 0.40f),
             new Vector3(0f, pylBase + pylonH * 0.17f, L * 0.05f), Vector3.zero, mat);
        // 顶部小托板（贴在机翼下表面，只比立柱宽一点点）
        Part(root, PrimitiveType.Cube, new Vector3(D * 0.52f, L * 0.008f, L * 0.34f),
             new Vector3(0f, pylBase + pylonH, L * 0.05f), Vector3.zero, mat);
    }

    static GameObject Part(Transform parent, PrimitiveType type, Vector3 scale, Vector3 pos, Vector3 euler, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = "MissilePart";
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;
        return go;
    }

    static Material MakeMat()
    {
        var m = new Material(Shader.Find("Standard"));
        m.color = new Color(0.62f, 0.63f, 0.64f);
        m.SetFloat("_Metallic", 0.35f);
        m.SetFloat("_Glossiness", 0.4f);
        return m;
    }

    /// <summary>打掉一枚（隐藏最上面那枚）。</summary>
    public void ConsumeOne()
    {
        if (_fired >= all.Count) return;
        var g = all[_fired];
        _fired++;
        if (g != null) g.SetActive(false);
    }

    /// <summary>复活：全部重新挂上。</summary>
    public void Reset()
    {
        _fired = 0;
        foreach (var g in all)
            if (g != null) g.SetActive(true);
    }
}
