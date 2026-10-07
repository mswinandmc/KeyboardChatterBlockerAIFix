using System.Collections.Concurrent;
using System.Text;

namespace KeyboardChatterBlocker;

/// <summary>
/// Lightweight, thread-safe diagnostic tracing used while validating the mod against a
/// specific ADOFAI build. Messages may be produced from any thread (the SkyHook native
/// callback runs on its own thread), so they are queued and flushed from the Unity main
/// thread in Main.OnUpdate before being handed to the UnityModManager logger.
/// Enabled through the "verboseLogging" setting.
/// </summary>
public static class Diagnostics
{
    private const int MaxQueued = 4096;

    private static readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();

    private static int dropped;

    private static int sequence;

    public static bool Enabled;

    public static int Dropped => dropped;

    public static void Trace(string message)
    {
        if (!Enabled)
        {
            return;
        }
        if (pending.Count >= MaxQueued)
        {
            dropped++;
            return;
        }
        pending.Enqueue("#" + (++sequence).ToString() + " " + message);
    }

    public static void Flush()
    {
        if (!Enabled)
        {
            return;
        }
        while (pending.TryDequeue(out string line))
        {
            Main.Logger?.Log("[diag] " + line);
        }
        if (dropped > 0)
        {
            int num = dropped;
            dropped = 0;
            Main.Logger?.Log("[diag] " + num + " diagnostic messages were dropped (queue full).");
        }
    }

    public static string Describe(object value)
    {
        if (value == null)
        {
            return "<null>";
        }
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append(value.GetType().Name);
        stringBuilder.Append("(");
        stringBuilder.Append(value.ToString());
        stringBuilder.Append(")");
        return stringBuilder.ToString();
    }
}
