using System;

namespace KeyboardChatterBlocker;

/// <summary>
/// Keeps a broken game build from turning into log spam and a broken input pipeline.
///
/// Harmony patches that throw are logged by Harmony for every single call, so a member that the
/// game renamed turns into hundreds of exceptions per second (this is exactly what happened when
/// ADOFAI 3.4.0 build 26044349 changed RDInput.GetMainPressKeys). Every patch body is therefore
/// guarded: the first exception is logged once with a stack trace, and after a handful of
/// failures the affected path disables itself so the game keeps playing with the mod inert
/// instead of throwing on every frame.
/// </summary>
public static class Failsafe
{
	private const int DisableThreshold = 25;

	private static int syncErrors;

	private static int asyncErrors;

	/// <summary>Set when synchronous (non SkyHook) blocking has been given up on.</summary>
	public static bool SyncDisabled { get; private set; }

	/// <summary>Set when asynchronous (SkyHook) blocking has been given up on.</summary>
	public static bool AsyncDisabled { get; private set; }

	public static void ReportSync(Exception exception)
	{
		syncErrors++;
		Log("sync", exception, syncErrors);
		if (!SyncDisabled && syncErrors >= DisableThreshold)
		{
			SyncDisabled = true;
			Main.Logger?.Error($"Synchronous input blocking disabled after {syncErrors} failures; this game build changed the API the path relies on.");
		}
	}

	public static void ReportAsync(Exception exception)
	{
		asyncErrors++;
		Log("async", exception, asyncErrors);
		if (!AsyncDisabled && asyncErrors >= DisableThreshold)
		{
			AsyncDisabled = true;
			Main.Logger?.Error($"Asynchronous input blocking disabled after {asyncErrors} failures; this game build changed the API the path relies on.");
		}
	}

	private static void Log(string path, Exception exception, int count)
	{
		if (count == 1)
		{
			Main.Logger?.Error($"[{path}] patch exception, reporting once: {exception.GetType().Name}: {exception.Message}");
			Main.Logger?.LogException(exception);
		}
		else if (count == DisableThreshold)
		{
			Main.Logger?.Error($"[{path}] {count} patch exceptions, disabling this path.");
		}
	}
}
