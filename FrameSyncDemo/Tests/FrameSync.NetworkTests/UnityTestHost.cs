using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

// Minimal host for executing the actual NetworkClient and transport outside Unity.
// Unity serialization/rendering are validated separately against real Unity assemblies.
namespace UnityEngine
{
    public class MonoBehaviour { }
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string text) { } }
    public sealed class SerializeField : Attribute { }
    public sealed class RangeAttribute : Attribute { public RangeAttribute(float min, float max) { } }
    public static class Application { public static int targetFrameRate; }
    public static class Time
    {
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        public static double realtimeSinceStartupAsDouble => clock.Elapsed.TotalSeconds;
    }
    public static class Random
    {
        private static readonly System.Random random = new System.Random(12345);
        public static int Range(int min, int max) => random.Next(min, max);
    }
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) => Console.WriteLine(message);
    }
    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions options = new JsonSerializerOptions { IncludeFields = true };
        public static string ToJson(object value) => JsonSerializer.Serialize(value, options);
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, options);
    }
}

internal sealed class MainThreadContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> queue = new();
    public override void Post(SendOrPostCallback callback, object state) => queue.Enqueue((callback, state));
    public void Pump()
    {
        while (queue.TryDequeue(out var work))
            work.Callback(work.State);
    }
}
