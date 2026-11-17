using UnityEngine;

/// <summary>
/// 验收辅助：持续把载具姿态钉在指定欧拉角（不阻塞主线程，让 LateUpdate 正常跑）。
///
/// 为什么需要：用 `System.Threading.Thread.Sleep()` 在 MCP 脚本里等帧，
/// 会**阻塞 Unity 主线程** —— LateUpdate 根本不会执行，测出来的相机状态全是残值。
/// 这个组件在 Update 里每帧重设姿态，脚本返回后由 Unity 自己跑帧，测的才是真实行为。
///
/// 只在 Dev 验收时挂，正常游戏不会用到（不挂在任何 prefab 上）。
/// </summary>
public class PoseHolder : MonoBehaviour
{
    public bool active;
    public float pitch, yaw, roll;

    void Update()
    {
        if (!active) return;
        transform.rotation = Quaternion.Euler(pitch, yaw, roll);
    }
}
