# KeyboardChatterBlocker — A Dance of Fire and Ice 3.4.0 移植说明

本分支把上游 [fangshenghan/KeyboardChatterBlocker](https://github.com/fangshenghan/KeyboardChatterBlocker) v0.1.2
（上游最后一次发布时标注 "Updated to 3.2"）移植到 **A Dance of Fire and Ice 3.4.0**，并针对 alpha 分支的
多次构建变更做了运行时兼容处理。

## 1. 目标环境

| 项目 | 版本 |
| --- | --- |
| 游戏 | A Dance of Fire and Ice **3.4.0**（Steam alpha 分支，`Application.version = 3.4.0`） |
| 已验证构建 | **2026-10-07**（Unity 6000.3.21f1，buildid 25590222）与 **2026-10-21**（Unity 6000.3.25f1，buildid 26044349） |
| 模组加载器 | UnityModManager **0.32.5** |
| 异步输入 | SkyHook（`skyhook.dll` + `SkyHook.Unity.dll`，随游戏更新） |
| 目标框架 | net48 / Unity Mono profile（引用游戏 `Managed` 目录内的程序集） |

> alpha 分支更新频繁，两次构建之间就可能出现二进制不兼容（见第 3 节）。模组因此采用
> **运行时探测 + 失效保护**，而不是把某一版签名写死在代码里。

## 2. 3.2 → 3.4.0 的变化（2026-10-07 构建）

对 `Assembly-CSharp.dll`（3.4.0）与更新前备份 `Assembly-CSharp.dll.BAK` 反编译后逐文件对比，与输入相关的改动：

* `AsyncInputManager` / `AsyncInputUtils` / `scrController.sortedKeyQueue` 的 tick 类型由 `ulong` 改为 `long`。
* `scrPlayer.HitInputEvent` 重写：`InputEventState` 枚举改为 `InputEventTarget`，改为按目标（Any/Action1/Action2/…）分发并返回 `!ignoreInput`。
* HitMargin 体系重构（新增 `HitMarginHelper`、`XPerfect`、`Midspin` 等），`scrMistakesManager.mistakesManager` 字段变动（这也是 LineKeyViewer 在当前版本持续报 `MissingFieldException` 的原因）。
* `SkyHook.Unity.dll` 更新：`SkyHookEvent` 是没有 setter 的 `readonly struct`，`SkyHookManager.HookCallback`（私有实例方法）改为触发 `SkyHookManager.KeyUpdated` 事件，`AsyncInputManager` 订阅该事件后入队。
* `scrPlayer.CountValidKeysPressed()` 同时负责：触摸输入计数、合作模式按玩家计数、`downKeysDuration` / `keyLimiterOverCounter` / `maximumUsedKeys` 簿记、`keyFrequency` / `keyTotal` 统计。

拦截点本身（`scrPlayer.CountValidKeysPressed`、`scrController.Awake/paused/gameworld/stateMachine`、
`SkyHookManager.HookCallback`、`SkyHookKeyMapper`、`AsyncInputManager.isActive`）在这些版本中依然存在。

## 3. 2026-10-21 构建（Unity 6000.3.25f1，buildid 26044349）的破坏性变更

这次更新把 **`RDInput.GetMainPressKeys()` 的签名从"返回新 List"改成了"由调用方传入 List 填充"**（减少 GC 分配）：

```csharp
// 旧（2026-10-07 及之前）
public static List<AnyKeyCode> GetMainPressKeys()
// 新（2026-10-21 起）
public static void GetMainPressKeys(List<AnyKeyCode> keys)   // GetMainHeldKeys 同理
```

对按旧签名编译的模组来说，这是**运行时**的 `MissingMethodException`：Harmony 补丁本身仍能挂上，
但每次进入 `scrPlayer.CountValidKeysPressed` 都抛异常。实测造成了 **520 次/局的异常刷屏**，
并且由于异常发生在输入判定链上（`scrController.UpdateInput → ProcessKeyInputs → Simulated_PlayerControl_Update
→ HitAutoFloors → ValidInputWasTriggered → CountValidKeysPressed`），表现为"模组直接死了"。

此外该构建把异步输入改成了事件驱动管线：`scrController.UpdateInput()` 把 `SkyHookManager.KeyUpdated`
投递的事件按 tick 排序，然后逐事件调用 `ProcessKeyInputs(long eventTick)`，因此 `CountValidKeysPressed`
现在在**异步模式下也会被调用**（旧版只在同步模式调用）。本模组的同步过滤只处理 `KeyCode`，
跳过 `AsyncKeyCode`，所以不会与异步路径重复拦截。

## 4. 本移植的改动

1. **兼容层 `GameCompat`**：用反射在运行时识别 `RDInput.GetMainPressKeys` 的两种形态
   （`void f(List<AnyKeyCode>)` / `List<AnyKeyCode> f()`），缓存为委托后每帧调用，零反射开销。
   日志会打印实际命中的形态：`Game compat: RDInput.GetMainPressKeys(List<AnyKeyCode>) [2026-10-21+ builds].`
   找不到兼容 API 时只在日志里警告一次，不再抛异常。
2. **失效保护 `Failsafe`**：所有补丁体都包在 try/catch 中。首次异常记录完整堆栈一次；
   同一路径累计 25 次异常后**自动停用该路径**（同步 / 异步各自独立），游戏不再被打断，也不会刷屏。
3. **同步输入路径"做减法"**：游戏原方法照常执行（保留 `keyFrequency`/`keyTotal`/按键限制器/触摸/合作簿记），
   `Postfix` 只把判定为 chatter 的按键数从结果里减掉，而不是整体覆盖 `__result`。
4. **Harmony 补丁失败不再静默**：`PatchAll` 抛错时写日志并禁用模组；成功时列出全部 5 个补丁目标。
5. **`AsyncInputManager.isActive` 访问加保护**（未进入 Play 时 `SkyHookManager.Instance` 为 `null`）。
6. **诊断开关 `verboseLogging`**（默认关闭，四语言界面标签）：记录 SkyHook 事件、`CountValidKeysPressed`
   的拦截结果、状态快照、兼容层/失效保护状态。⚠️ 会记录所有按键事件，排查完请关闭。
7. **构建方式**：`csproj` 引用路径参数化（`AdofaiDir`），新增 `build.ps1` / `package.ps1`，
   不装 .NET SDK 也能用 Roslyn `csc.exe` 直接针对游戏程序集编译。
8. **版本号** `0.1.2 → 0.1.3 → 0.2.0`，`Info.json` 含 `GameVersion: 3.4.0`。

## 5. 验证记录

### 2026-10-21 构建（Unity 6000.3.25f1 / buildid 26044349），模组 0.2.0

```
[KeyboardChatterBlocker] KeyboardChatterBlocker 0.2.0 loading. Game 3.4.0, Unity 6000.3.25f1, 64-bit.
[KeyboardChatterBlocker] Game compat: RDInput.GetMainPressKeys(List<AnyKeyCode>) [2026-10-21+ builds].
[KeyboardChatterBlocker] Harmony patches applied to 5 methods:
[KeyboardChatterBlocker]   scrPlayer.CountValidKeysPressed
[KeyboardChatterBlocker]   UnityEngine.Input.GetKeyDown
[KeyboardChatterBlocker]   UnityEngine.Input.GetKey
[KeyboardChatterBlocker]   SkyHook.SkyHookManager.HookCallback
[KeyboardChatterBlocker]   scrController.Awake
[KeyboardChatterBlocker] Active.
[JipperResourcePack INFO] KeyboardChatterBlockerAPI is loaded.
```

* **异常清零**：Player.log 中 `MissingMethodException` / `MissingFieldException` = **0**，KCB 堆栈帧 = **0**
  （修复前同一构建为 520 次 `MissingMethodException: RDInput.GetMainPressKeys`）。
* **异步拦截（实测）**：注入 25 ms 间隔连击，正确拦截：
  ```
  [KeyboardChatterBlocker] Blocked Async Key: K time: 27ms.
  [KeyboardChatterBlocker] Blocked Async Key: K time: 30ms.
  ```
* **同步拦截（实测）**：`CountValidKeysPressed` 走到新 API 并正确做减法：
  ```
  [KeyboardChatterBlocker] [diag] CountValidKeysPressed call #24: game=1 blocked=1 -> 0
  [KeyboardChatterBlocker] Blocked Key: Mouse0 time: 11ms.
  ```

### 2026-10-07 构建（Unity 6000.3.21f1 / buildid 25590222），模组 0.1.3

* 5 个 Harmony 补丁全部命中；异步拦截实测 `Blocked Async Key: K time: 24ms.` 等；
  同步路径 `CountValidKeysPressed` 被命中，`game=1 blocked=0`。
* 未覆盖：真实游玩中 `gameworld=true` 的端到端手动确认（需玩家本人试玩）。

## 6. 构建与安装

```powershell
# 一键打包（首次可加 -DownloadCompiler 自动下载 Roslyn 编译器）
./package.ps1 -GameDir "D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice"

# 或只编译
./build.ps1 -GameDir "..." -OutDir "dist\KeyboardChatterBlocker"

# 有 .NET SDK / VS 时也可以正常走 csproj
dotnet build -p:AdofaiDir="D:\...\A Dance of Fire and Ice"
```

产物：`dist/KeyboardChatterBlocker/{Info.json, KeyboardChatterBlocker.dll}`，以及 `dist/KeyboardChatterBlocker.v0.2.0.zip`。
安装：把 `KeyboardChatterBlocker` 文件夹复制到 `<游戏目录>/Mods/`（或在 UMM 中安装 zip），然后在 UMM 里启用。

## 7. 已知限制

* 异步按键以 SkyHook 的原生键值（Windows 虚拟键码）存储；Linux 上原生键值与 Windows 不同（上游 issue #3），本移植未改动该行为。
* 合作（coop）模式下按键归属仍按全局统计，与上游行为一致。
* `Info.json` 的 `GameVersion` 表示"最低游戏版本"；当前 UMM 配置未启用版本点解析，因此不会阻止加载。
* `verboseLogging` 会记录全部按键事件，默认关闭。
* 若后续构建再次改动被拦截的方法名/签名，Harmony 补丁会失败并写入日志，模组会安全地保持未启用状态，而不是崩溃。
