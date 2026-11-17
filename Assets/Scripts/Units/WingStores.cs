using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 机翼挂架上的外挂物（导弹）。打一发少一枚，复活时重新挂满。
///
/// ⚠️⚠️⚠️ 2026-11-17 14:45 用户："我说的是空空导弹不是反坦克导弹" + "这一次空空导弹看不见了"
///
/// **战斗机模型和攻击机完全不是一个尺度**（MCP 实测）：
///   攻击机：网格 bounds **±8.80**（已经是真实米），Model 缩放 **0.60**
///   战斗机：网格 bounds **±0.04**（厘米级），Model 缩放 **100.00**
///
/// 之前所有长度都是写死的米数常数（`bestZ - 0.18`、`+0.25f` 等）。
/// 在战斗机的微缩空间里，0.18 相当于 **18 米** —— 导弹被甩到 18 米外，
/// MCP 实测 `up = -18.00`，完全看不见。
///
/// ✅ 现在**所有长度都按模型实际世界尺寸的比例算**，不写死任何米数。
///
/// 为什么在 Start 里建而不是在 VehicleModelBinder 里当场建：
/// 模型实例化后还要经过 DropToGround 重新定位，在 Attach 阶段算世界坐标会少掉那段偏移。
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

    int _fired;
    bool _built;

    /// <summary>还剩几枚（视觉）。</summary>
    public int Remaining => Mathf.Max(0, all.Count - _fired);

    void Start()
    {
        if (!_built && model != null) BuildProps();
    }

    /// <summary>按飞机实际尺寸挂弹。</summary>
    ///
    /// ⚠️⚠️⚠️ 2026-11-17 15:00 用户："越修越坏" + 截图（导弹整体脱开飘在空中）。
    ///
    /// **彻底换思路**：前几版一直在**网格空间**里算坐标，再用 `mf.transform.TransformPoint`
    /// 转世界。但模型节点的 rotation=(270,90,0)、lossyScale 从 0.6 到 100 不等，
    /// 任何一处搞错（旋转顺序、缩放、轴向映射）都会让导弹整体飞出去 —— 越改越乱。
    ///
    /// ✅ 新做法：**完全在飞机根节点（transform）的局部空间里摆弹**，
    ///    坐标语义直接用飞机的自身轴：
    ///       localPos.x = 右（+right）
    ///       localPos.y = 上（+up）
    ///       localPos.z = 前（+forward）
    ///    尺寸只从"飞机世界翼展"取一个标量，再换算成本地单位。
    ///    **不碰网格顶点、不做 TransformPoint**，从根上消除变换组合出错的可能。
    public void BuildProps()
    {
        _built = true;
        var mf = model.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        var useMat = mat != null ? mat : MakeMat();

        // ---- 只取一个标量：飞机在世界里的实际翼展（米）----
        var mb = mf.sharedMesh.bounds;
        Vector3 lossy = mf.transform.lossyScale;
        Vector3 worldSize = new Vector3(
            Mathf.Abs(mb.size.x * lossy.x),
            Mathf.Abs(mb.size.y * lossy.y),
            Mathf.Abs(mb.size.z * lossy.z));
        float spanWorld = Mathf.Max(worldSize.x, Mathf.Max(worldSize.y, worldSize.z));
        if (spanWorld < 0.01f) spanWorld = 10f;      // 兜底

        // 飞机根节点的世界缩放（把"世界米"换成"根节点本地单位"）
        Vector3 rootScale = transform.lossyScale;
        float s = Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(rootScale.x),
                            Mathf.Max(Mathf.Abs(rootScale.y), Mathf.Abs(rootScale.z))));

        // 弹体尺寸（世界米 → 根节点本地单位）
        float missileLenWorld = Mathf.Clamp(spanWorld * 0.062f, 0.6f, 2.2f);
        float missileDiaWorld = missileLenWorld * 0.145f;
        float L = missileLenWorld / s;               // 本地单位
        float D = missileDiaWorld / s;

        // 挂点：翼展比例（相对半翼展），机身下方偏移（本地单位）
        float halfSpanLocal = (spanWorld * 0.5f) / s;
        float downOffset = (missileDiaWorld * 1.35f) / s;   // 机翼下表面再往下一点

        int count = total > 0 ? total : perSide * 2;
        int idx = 0;

        for (int i = 0; i < count; i++)
        {
            int side = (i % 2 == 0) ? -1 : 1;
            int row = i / 2;

            Vector3 localPos;
            if (count == 1)
            {
                // 单发：机腹中线（用户 2026-11-17 明确要求"空空导弹放机腹中线"）
                localPos = new Vector3(0f, -downOffset, 0f);
            }
            else
            {
                // 多发：机翼下，左右交替，从内到外铺开
                int perSideCount = Mathf.Max(1, Mathf.CeilToInt(count / 2f));
                float t = perSideCount <= 1
                    ? 0.62f
                    : Mathf.Lerp(0.42f, 0.82f, row / (float)(perSideCount - 1));
                localPos = new Vector3(side * halfSpanLocal * t, -downOffset, 0f);
            }

            var m = new GameObject($"Missile_{idx}");
            m.transform.SetParent(transform, false);
            // 局部坐标直接用飞机的自身轴（x=右 y=上 z=前），identity 旋转
            m.transform.localPosition = localPos;
            m.transform.localRotation = Quaternion.identity;

            // 弹体：沿本地 +Z（= 飞机前方）
            Part(m.transform, PrimitiveType.Cylinder, new Vector3(D, L * 0.5f, D),
                 new Vector3(0f, 0f, L * 0.5f), new Vector3(90f, 0f, 0f), useMat);
            // 弹头
            Part(m.transform, PrimitiveType.Sphere, new Vector3(D, D * 1.35f, D),
                 new Vector3(0f, 0f, L * 1.02f), Vector3.zero, useMat);
            // 尾翼（十字）
            Part(m.transform, PrimitiveType.Cube, new Vector3(L * 0.38f, L * 0.018f, L * 0.18f),
                 new Vector3(0f, 0f, L * 0.09f), Vector3.zero, useMat);
            Part(m.transform, PrimitiveType.Cube, new Vector3(L * 0.018f, L * 0.35f, L * 0.18f),
                 new Vector3(0f, 0f, L * 0.09f), Vector3.zero, useMat);
            // 挂架（从弹体往上连到机翼/机身）
            Part(m.transform, PrimitiveType.Cube, new Vector3(L * 0.07f, L * 0.30f, L * 0.36f),
                 new Vector3(0f, L * 0.28f, L * 0.36f), Vector3.zero, useMat);

            all.Add(m);
            idx++;
        }
    }

    // 【已删除】旧的 WingUnderside（网格空间求机翼下表面）。
    //
    // 2026-11-17 15:00 彻底弃用：它把"网格顶点坐标"当挂点，再经 mf.transform.TransformPoint
    // 转世界。模型节点 rotation=(270,90,0)、lossyScale 0.6~100 不等，
    // 任何一处搞错都让导弹整体飞出去（用户截图："越修越坏"、"怎么都脱离了"）。
    // 现在改为**直接在飞机根节点局部空间摆弹**（x=右 y=上 z=前），不碰网格顶点。

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
