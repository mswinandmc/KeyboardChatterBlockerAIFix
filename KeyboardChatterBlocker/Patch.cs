using System;
using System.Collections.Generic;
using HarmonyLib;
using SkyHook;
using UnityEngine;
using static UnityModManagerNet.UnityModManager.ModEntry;

namespace KeyboardChatterBlocker;

public static class Patch
{
	/// <summary>Number of SkyHook events seen by the mod (diagnostics).</summary>
	public static int hookEventCount;

	/// <summary>Number of async key presses the mod suppressed (diagnostics).</summary>
	public static int blockedAsyncCount;

	/// <summary>Number of synchronous key presses the mod suppressed (diagnostics).</summary>
	public static int blockedSyncCount;

	/// <summary>Number of scrPlayer.CountValidKeysPressed calls intercepted (diagnostics).</summary>
	public static int countValidKeysCalls;

	private static int tracedHookEvents;

	private static scrPlayer PlayerOne
	{
		get
		{
			return CachedInstance.playerOne;
		}
	}

    private static readonly WeakReference<scrController> _cachedInstanceRef = new WeakReference<scrController>(null);
    private static scrController CachedInstance
    {
        get
        {
            if (_cachedInstanceRef.TryGetTarget(out var target) && target != null)
            {
                return target;
            }
            return null;
        }
        set
        {
            _cachedInstanceRef.SetTarget(value);
        }
    }

    /// <summary>Reads the cached controller without exposing the weak reference itself.</summary>
    public static bool TryGetController(out scrController controller)
    {
        controller = CachedInstance;
        return controller != null;
    }

    /// <summary>
    /// Synchronous (non SkyHook) input path.
    ///
    /// The game calls scrPlayer.CountValidKeysPressed() from ValidInputWasTriggered() and from the
    /// hold/auto floor handling, so suppressing a chatter repeat there removes the bogus hit.
    /// Because the game build keeps growing this method (touch input, co-op counting, per-floor
    /// key limiter bookkeeping in 3.4.0), the mod no longer replaces the result: the game's own
    /// body still runs and keeps keyFrequency/keyTotal/keyLimiterOverCounter/downKeysDuration up
    /// to date, and the postfix only subtracts the presses recognised as chatter.
    /// </summary>
    [HarmonyPatch(typeof(scrPlayer), "CountValidKeysPressed")]
	public static class scrPlayer_CountValidKeysPressed_Patch
	{
		public static void Prefix(out int __state)
		{
			countValidKeysCalls++;
			__state = CountChatterRepeats();
		}

		public static void Postfix(ref int __result, int __state)
		{
			if (__state > 0)
			{
				int gameResult = __result;
				__result = Math.Max(0, __result - __state);
				if (Main.setting.verboseLogging)
				{
					Diagnostics.Trace($"CountValidKeysPressed call #{countValidKeysCalls}: game={gameResult} blocked={__state} -> {__result}");
				}
			}
			else if (Main.setting.verboseLogging && (countValidKeysCalls < 10 || countValidKeysCalls % 300 == 0))
			{
				Diagnostics.Trace($"CountValidKeysPressed call #{countValidKeysCalls}: game={__result} blocked=0");
			}
		}
	}

    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) })]
    public static class Unity_GetKeyDown_Patch
    {
        public static bool Prefix(KeyCode key, ref bool __result)
        {
            if (Main.isListeningKeysIgnore || Main.isListeningKeysLimiter)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }


    [HarmonyPatch(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) })]
    public static class Unity_GetKey_Patch
    {
        public static bool Prefix(KeyCode key, ref bool __result)
        {
            if (Main.isListeningKeysIgnore || Main.isListeningKeysLimiter)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Asynchronous input path (SkyHook). SkyHookManager.HookCallback is the single entry point
    /// for native key events in ADOFAI 3.4.0 - it forwards to KeyUpdated, which AsyncInputManager
    /// turns into its key queue - so returning false here drops the event before the game can
    /// ever see it.
    /// </summary>
    [HarmonyPatch(typeof(SkyHookManager), "HookCallback")]
	public static class SkyHookManager_HookCallback_Patch
	{
		public static bool Prefix(SkyHookEvent ev)
		{
            hookEventCount++;
            if (Main.setting.verboseLogging && tracedHookEvents < 40)
            {
                tracedHookEvents++;
                long traceNow = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                long traceDelta = lastKeyPressAsync.TryGetValue(ev.Key, out long tracePrevious) && tracePrevious > 0 ? traceNow - tracePrevious : -1L;
                Diagnostics.Trace($"hook event #{hookEventCount}: type={ev.Type} key={ev.Key} label={ev.Label} delta={traceDelta}ms interval={Main.setting.inputInterval} ignored={Main.setting.ignoredAsyncKeys.Contains(ev.Key)} listening={Main.isListeningKeysIgnore}/{Main.isListeningKeysLimiter}");
            }

            if (Main.isListeningKeysIgnore)
            {
                if (ev.Type == SkyHook.EventType.KeyPressed && !Utils.MouseKeys.Contains(ev.Label))
                {
                    if (Main.setting.ignoredAsyncKeys.Contains(ev.Key))
                        Main.setting.ignoredAsyncKeys.Remove(ev.Key);
                    else
                        Main.setting.ignoredAsyncKeys.Add(ev.Key);
                    Main.UpdateSelectedKeyLimiterProfile();
                }
                return false; 
            }

            if (Main.isListeningKeysLimiter)
            {
                if (ev.Type == SkyHook.EventType.KeyPressed && !Utils.MouseKeys.Contains(ev.Label))
                {
                    if (Main.listeningKeyLimiterProfile.allowedAsyncKeys.Contains(ev.Key))
                        Main.listeningKeyLimiterProfile.allowedAsyncKeys.Remove(ev.Key);
                    else
                        Main.listeningKeyLimiterProfile.allowedAsyncKeys.Add(ev.Key);
                    Main.UpdateSelectedKeyLimiterProfile();
                }
                return false; 
            }

            if (CachedInstance is null)
			{
				return true;
			}
			if (ev.Type == SkyHook.EventType.KeyReleased || ev.Key == 27)
			{
				return true;
			}
			if (Main.setting.enableKeyLimiter && !Main.selectedKeyLimiterProfile.allowedAsyncKeys.Contains(ev.Key) && !CachedInstance.paused && CachedInstance.gameworld)
			{
				Enum state = CachedInstance.stateMachine.GetState();
				if (state is States && (States)(object)state == States.PlayerControl)
				{
					blockedAsyncCount++;
					return false;
				}
			}
			if (Main.setting.ignoredAsyncKeys.Contains(ev.Key))
			{
				return true;
			}
			if (!lastKeyPressAsync.ContainsKey(ev.Key))
			{
				lastKeyPressAsync.Add(ev.Key, 0L);
			}
			long num = DateTimeOffset.Now.ToUnixTimeMilliseconds();
			long num2 = num - lastKeyPressAsync[ev.Key];
			if (num2 > Main.setting.inputInterval)
			{
				lastKeyPressAsync[ev.Key] = num;
				return true;
			}
			ModLogger logger = Main.Logger;
			string[] obj = ["Blocked Async Key: ", null, null, null, null];
			KeyLabel label = ev.Label;
			obj[1] = label.ToString();
			obj[2] = " time: ";
			obj[3] = num2.ToString();
			obj[4] = "ms.";
			logger.Log(string.Concat(obj));
			blockedAsyncCount++;
			return false;
		}
	}

    [HarmonyPatch(typeof(scrController), "Awake")]
    private static class scrController_Awake_Patch
    {
        public static void Postfix(scrController __instance)
        {
            CachedInstance = __instance;
            if (Main.setting.verboseLogging)
            {
                Diagnostics.Trace($"scrController.Awake -> cached instance ({__instance.name}, gameworld={__instance.gameworld})");
            }
        }
    }

    public static Dictionary<ushort, long> lastKeyPressAsync = new Dictionary<ushort, long>();

	public static Dictionary<KeyCode, long> lastKeyPress = new Dictionary<KeyCode, long>();

	public static Dictionary<ushort, long> countedAsync = new Dictionary<ushort, long>();

	public static Dictionary<KeyCode, long> counted = new Dictionary<KeyCode, long>();

	/// <summary>
	/// Number of main keys pressed during this frame that must not be counted as a valid input.
	/// Async keys are excluded on purpose: they are filtered in the SkyHook callback where the
	/// event can be dropped before the game queues it.
	/// </summary>
	private static int CountChatterRepeats()
	{
		if (!CachedInstance)
		{
			return 0;
		}
		int blocked = 0;
		long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		scrPlayer playerOne = PlayerOne;
		foreach (AnyKeyCode mainPressKey in RDInput.GetMainPressKeys())
		{
			if (!(mainPressKey.value is KeyCode keyCode))
			{
				continue;
			}

			// Key limiter: keys outside the selected profile are not allowed to hit at all.
			if (Main.setting.enableKeyLimiter && !Main.selectedKeyLimiterProfile.allowedKeys.Contains(keyCode) && !CachedInstance.paused && CachedInstance.gameworld)
			{
				Enum state = CachedInstance.stateMachine.GetState();
				if (state is States && (States)(object)state == States.PlayerControl)
				{
					blocked++;
					continue;
				}
			}

			// Ignored keys are never filtered by the chatter threshold.
			if (Main.setting.ignoredKeys.Contains(keyCode))
			{
				continue;
			}

			if (!lastKeyPress.ContainsKey(keyCode))
			{
				lastKeyPress.Add(keyCode, 0L);
			}
			long delta = now - lastKeyPress[keyCode];
			// A repeat inside the threshold is chatter; repeats within a few milliseconds are the
			// same physical press being reported twice and stay valid.
			if (delta > Main.setting.inputInterval || delta <= 5)
			{
				lastKeyPress[keyCode] = now;
				continue;
			}

			Main.Logger.Log("Blocked Key: " + keyCode.ToString() + " time: " + delta + "ms.");
			blockedSyncCount++;
			blocked++;
		}
		_ = playerOne;
		return blocked;
	}
}
