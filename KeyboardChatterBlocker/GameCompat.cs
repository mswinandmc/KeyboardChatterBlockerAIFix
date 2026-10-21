using System;
using System.Collections.Generic;
using System.Reflection;

namespace KeyboardChatterBlocker;

/// <summary>
/// Bridges the small binary-breaking changes the ADOFAI alpha branch introduces between builds.
///
/// The 3.4.0 build shipped on 2026-10-21 (Unity 6000.3.25f1) changed
///     List&lt;AnyKeyCode&gt; RDInput.GetMainPressKeys()
/// into
///     void RDInput.GetMainPressKeys(List&lt;AnyKeyCode&gt; keys)
/// which turns the call compiled against the previous build into a MissingMethodException at
/// runtime (it spammed the player log once per frame and broke the input pipeline).
/// The shape is resolved once through reflection and invoked through a cached delegate so the
/// mod keeps working on both the old and the new signature - and survives the next reshuffle.
/// </summary>
public static class GameCompat
{
	private const string PressKeyMethodName = "GetMainPressKeys";

	private static Action<List<AnyKeyCode>> fillStyle;

	private static Func<List<AnyKeyCode>> returnStyle;

	private static bool resolved;

	private static bool warned;

	private static string description = "not resolved yet";

	/// <summary>Human readable description of the resolved shapes, shown in the log.</summary>
	public static string Description
	{
		get
		{
			Resolve();
			return description;
		}
	}

	/// <summary>True when a compatible RDInput.GetMainPressKeys overload was found.</summary>
	public static bool HasPressKeyEnumeration
	{
		get
		{
			Resolve();
			return fillStyle != null || returnStyle != null;
		}
	}

	/// <summary>
	/// Returns the main keys pressed during the current frame, either by filling
	/// <paramref name="buffer"/> (new builds) or by returning a fresh list (old builds).
	/// Returns null when the running build no longer exposes a compatible API.
	/// </summary>
	public static List<AnyKeyCode> GetMainPressKeys(List<AnyKeyCode> buffer)
	{
		Resolve();
		if (fillStyle != null)
		{
			buffer.Clear();
			fillStyle(buffer);
			return buffer;
		}
		if (returnStyle != null)
		{
			return returnStyle();
		}
		return null;
	}

	/// <summary>Logs a single warning when synchronous blocking cannot work on this build.</summary>
	public static void WarnIfUnavailable()
	{
		if (warned || HasPressKeyEnumeration)
		{
			return;
		}
		warned = true;
		Main.Logger?.Error("Synchronous input blocking is unavailable on this game build: " + description + ". Async (SkyHook) blocking is unaffected.");
	}

	private static void Resolve()
	{
		if (resolved)
		{
			return;
		}
		resolved = true;
		try
		{
			Type inputType = Type.GetType("RDInput, Assembly-CSharp");
			if (inputType == null)
			{
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					if (assembly.GetName().Name != "Assembly-CSharp")
					{
						continue;
					}
					inputType = assembly.GetType("RDInput");
					break;
				}
			}
			if (inputType == null)
			{
				description = "RDInput type not found";
				return;
			}

			MethodInfo[] methods = inputType.GetMethods(BindingFlags.Public | BindingFlags.Static);
			foreach (MethodInfo method in methods)
			{
				if (method.Name != PressKeyMethodName)
				{
					continue;
				}
				ParameterInfo[] parameters = method.GetParameters();
				if (parameters.Length == 1 && parameters[0].ParameterType == typeof(List<AnyKeyCode>) && method.ReturnType == typeof(void))
				{
					fillStyle = (Action<List<AnyKeyCode>>)Delegate.CreateDelegate(typeof(Action<List<AnyKeyCode>>), method);
					description = "RDInput.GetMainPressKeys(List<AnyKeyCode>) [2026-10-21+ builds]";
					return;
				}
				if (parameters.Length == 0 && method.ReturnType == typeof(List<AnyKeyCode>))
				{
					returnStyle = (Func<List<AnyKeyCode>>)Delegate.CreateDelegate(typeof(Func<List<AnyKeyCode>>), method);
					description = "RDInput.GetMainPressKeys() [pre 2026-10-21 builds]";
					return;
				}
			}
			description = "no compatible RDInput.GetMainPressKeys overload";
		}
		catch (Exception exception)
		{
			description = "RDInput.GetMainPressKeys resolution failed: " + exception.GetType().Name + ": " + exception.Message;
		}
	}
}
