using UnityEngine;
using TMPro; // 用 UI Text 的话换成 UnityEngine.UI 和 Text

public class FPSDisplay : MonoBehaviour
{
    public TMP_Text label;
    public float refreshInterval = 0.3f;

    private float _timer, _accumTime;
    private int _frames;

    void Update()
    {
        _timer += Time.unscaledDeltaTime;
        _accumTime += Time.unscaledDeltaTime;
        _frames++;

        if (_timer >= refreshInterval)
        {
            float avgFrameTime = _accumTime / _frames;   // 平均每帧耗时
            float fps = 1f / avgFrameTime;               // 比 frames/timer 更平滑
            label.text = $"FPS: {fps:F0}\n{avgFrameTime * 1000f:F1} ms";

            _timer = 0f; _accumTime = 0f; _frames = 0;
        }
    }
}