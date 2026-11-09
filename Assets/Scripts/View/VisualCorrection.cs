
using FrameSync.Shared;
using UnityEngine;

/// <summary>
/// 只负责视觉平滑，不修改GameWorld
/// </summary>
public sealed class VisualCorrection
{
    private readonly Transform[] views;
    private readonly Vector3[] offsets;

    private readonly float halfLife;
    private readonly float snapDistance;

    public VisualCorrection(Transform player0, Transform player1, float halfLife = 0.08f, float snapDistance = 2f)
    {
        views = new Transform[] { player0, player1 };
        offsets = new Vector3[GameWorld.PlayerCount];

        this.halfLife = Mathf.Max(0.001f, halfLife);
        this.snapDistance = Mathf.Max(0f, snapDistance);
    }

    private static Vector3 ToPosition(PlayerState state, float z)
    {
        return new Vector3(
            state.X / (float)GameWorld.PositionScale,
            state.Y / (float)GameWorld.PositionScale,
            z
        );
    }

    /// <summary>
    /// 初始化视觉位置
    /// </summary>
    public void Snap(PlayerState[] states)
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            if (views[i] == null)
                continue;

            views[i].position = ToPosition(states[i], views[i].position.z);
            offsets[i] = Vector3.zero;
        }
    }

    /// <summary>
    /// 发生回滚后重新计算视觉修正偏移
    /// </summary>
    public void OnRollback(PlayerState[] correctedStates, int predictedPlayerId = -1)
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            if ((predictedPlayerId >= 0 && i != predictedPlayerId) || views[i] == null)
                continue;

            Vector3 corrected = ToPosition(correctedStates[i], views[i].position.z);

            // 当前实际渲染位置与正确逻辑位置的差值
            Vector3 error = views[i].position - corrected;

            if (error.magnitude > snapDistance)
            {
                // 误差过大时直接纠正
                offsets[i] = Vector3.zero;
                views[i].position = corrected;
            }
            else
            {
                offsets[i] = error;
            }
        }
    }

    /// <summary>
    /// 每个Unity渲染帧更新视觉位置
    /// </summary>
    public void Render(PlayerState[] previous, PlayerState[] current, float alpha, float deltaTime, int predictedPlayerId = -1)
    {
        alpha = Mathf.Clamp01(alpha);
        deltaTime = Mathf.Max(0f, deltaTime);

        // 每经过一个halfLife，修正偏移减少一半
        float decay = Mathf.Pow(0.5f, deltaTime / halfLife);

        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            Transform view = views[i];

            if ((predictedPlayerId >= 0 && i != predictedPlayerId) || view == null)
                continue;

            Vector3 from = ToPosition(previous[i], view.position.z);
            Vector3 to = ToPosition(current[i], view.position.z);

            Vector3 interpolated = Vector3.Lerp(from, to, alpha);

            offsets[i] *= decay;

            view.position = interpolated + offsets[i];
        }
    }
}
