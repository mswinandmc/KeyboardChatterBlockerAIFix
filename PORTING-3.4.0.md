# KeyboardChatterBlocker — A Dance of Fire and Ice 3.4.0 移植说明

本分支把上游 [fangshenghan/KeyboardChatterBlocker](https://github.com/fangshenghan/KeyboardChatterBlocker) v0.1.2
（上游最后一次发布时标注 "Updated to 3.2"）移植到 **A Dance of Fire and Ice 3.4.0**。

## 1. 目标环境

| 项目 | 版本 |
| --- | --- |
| 游戏 | A Dance of Fire and Ice **3.4.0**（Steam alpha 分支，`Application.version = 3.4.0`） |
| 引擎 | Unity **6000.3.21f1**（Mono，非 IL2CPP） |
| 模组加载器 | UnityModManager **0.32.5** |
| 异步输入 | SkyHook（`skyhook.dll` + `SkyHook.Unity.dll`，随游戏更新） |
| 目标框架 | net48 / Unity Mono profile（引用游戏 `Managed` 目录内的程序集） |

## 2. 3.3 / 3.4.0 相比 3.2 的相关变化

对 `Assembly-CSharp.dll`（3.4.0）与更新前备份 `Assembly-CSharp.dll.BAK` 反编译后逐文件对比，与输入相关的改动：

* `AsyncInputManager` / `AsyncInputUtils` / `scrController.sortedKeyQueue` 的 tick 类型由 `ulong` 改为 `long`。
* `scrPlayer.HitInputEvent` 重写：`InputEventState` 枚举改为 `InputEventTarget`，改为按目标（Any/Action1/Action2/…）分发并返回 `!ignoreInput`。
* HitMargin 体系重构（新增 `HitMarginHelper`、`XPerfect`、`Midspin` 等），`scrMistakesManager.mistakesManager` 字段变动（这也是 LineKeyViewer 在当前版本持续报 `MissingFieldException` 的原因）。
* `SkyHook.Unity.dll` 更新：`SkyHookEvent` 现在是没有 setter 的 `readonly struct`，`SkyHookManager.HookCallback`（私有实例方法）改为触发 `SkyHookManager.KeyUpdated` 事件，`AsyncInputManager` 订阅该事件后入队。
* `scrPlayer.CountValidKeysPressed()` 在 3.4.0 中同时负责：触摸输入计数、合作模式按玩家计数、`downKeysDuration` / `keyLimiterOverCounter` / `maximumUsedKeys` 簿记、`keyFrequency` / `keyTotal` 统计。

好消息是本模组依赖的所有符号（`scrPlayer.CountValidKeysPressed`、`RDInput.GetMainPressKeys`、`scrController.Awake/paused/gameworld/stateMachine`、
`SkyHookManager.HookCallback`、`SkyHookKeyMapper`、`AsyncInputManager.isActive`）在 3.4.0 中依然存在，因此拦截点无需改变。

## 3. 本移植的改动

1. **同步输入路径改为"做减法"，不再整体替换游戏结果**
   旧实现是 `Prefix` 里自行统计一次、`Postfix` 里把 `__result` 整体覆盖，同时自己又累加了一遍 `keyFrequency` / `keyTotal`（与游戏本体重复计数），并且让 3.4.0 新增的触摸 / 合作 / `downKeysDuration` / `maximumUsedKeys` 逻辑全部失效。
   现在游戏原方法照常执行（保留它自己的簿记），`Postfix` 只把被判定为 chatter 的按键数从结果里减掉；单人 + 键盘情况下结果与旧实现完全一致，其余情况不再破坏游戏逻辑。
2. **Harmony 补丁失败不再静默**
   `PatchAll` 会因某个目标方法缺失而整体失败。现在会捕获异常、写入 UMM 日志并禁用模组，成功时在日志中列出全部 5 个补丁目标，便于日后游戏更新后第一时间定位。
3. **`AsyncInputManager.isActive` 访问加保护**
   该属性内部解引用 `SkyHookManager.Instance`，在未进入 Play 状态时为 `null`，直接访问会抛 `NullReferenceException`。现在统一走 `Main.IsAsyncInputActive()`。
4. **新增诊断开关 `verboseLogging`（默认关闭，四语言界面标签）**
   开启后会在 UMM 日志中记录：SkyHook 事件（类型 / 键值 / 距上次按下间隔）、`CountValidKeysPressed` 的拦截结果、控制器状态快照、被拦截次数。跨线程消息先入队、由主线程统一写出。
   ⚠️ 诊断日志会记录所有按键事件，排查完请关闭。
5. **构建方式**
   目标框架与引用路径参数化（`AdofaiDir`），并新增 `build.ps1` / `package.ps1`：不装 .NET SDK 也能用 Roslyn `csc.exe` 直接针对游戏程序集编译。
6. **版本号** `0.1.2 → 0.1.3`，`Info.json` 增加 `GameVersion: 3.4.0`。

## 4. 验证记录

在 3.4.0（alpha，Unity 6000.3.21f1）实机运行，UMM 日志证据：

```
[KeyboardChatterBlocker] KeyboardChatterBlocker 0.1.2 loading. Game 3.4.0, Unity 6000.3.21f1, 64-bit.   # 版本号取自 Info.json，打包后为 0.1.3
[KeyboardChatterBlocker] Harmony patches applied to 5 methods:
[KeyboardChatterBlocker]   scrPlayer.CountValidKeysPressed
[KeyboardChatterBlocker]   UnityEngine.Input.GetKeyDown
[KeyboardChatterBlocker]   UnityEngine.Input.GetKey
[KeyboardChatterBlocker]   SkyHook.SkyHookManager.HookCallback
[KeyboardChatterBlocker]   scrController.Awake
[KeyboardChatterBlocker] Active.
```

* **异步输入拦截（实测通过）**：向运行中的游戏注入 15–30 ms 间隔的重复按键，mod 正确拦截并记录：
  ```
  [KeyboardChatterBlocker] Blocked Async Key: K time: 24ms.
  [KeyboardChatterBlocker] Blocked Async Key: K time: 42ms.
  [KeyboardChatterBlocker] Blocked Async Key: K time: 16ms.
  ```
* **同步输入拦截（实测通过）**：`scrPlayer.CountValidKeysPressed` 已被命中并带出游戏自身返回值：
  ```
  [KeyboardChatterBlocker] [diag] CountValidKeysPressed call #5: game=1 blocked=0
  ```
* **未覆盖**：同步路径在真实游玩（`gameworld=true` 且处于 `PlayerControl`）中的端到端表现，需要在游戏内连续快速敲击同一键做人工确认。

## 5. 构建与安装

```powershell
# 一键打包（首次可加 -DownloadCompiler 自动下载 Roslyn 编译器）
./package.ps1 -GameDir "D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice"

# 或只编译
./build.ps1 -GameDir "..." -OutDir "dist\KeyboardChatterBlocker"

# 有 .NET SDK / VS 时也可以正常走 csproj
dotnet build -p:AdofaiDir="D:\...\A Dance of Fire and Ice"
```

产物：`dist/KeyboardChatterBlocker/{Info.json, KeyboardChatterBlocker.dll}`，以及 `dist/KeyboardChatterBlocker.v0.1.3.zip`。
安装：把 `KeyboardChatterBlocker` 文件夹复制到 `<游戏目录>/Mods/`（或在 UMM 中安装 zip），然后在 UMM 里启用。

## 6. 已知限制

* 异步按键以 SkyHook 的原生键值（Windows 虚拟键码）存储；Linux 上原生键值与 Windows 不同（上游 issue #3），本移植未改动该行为。
* 合作（coop）模式下按键归属仍按全局统计，与上游行为一致。
* `Info.json` 的 `GameVersion` 表示"最低游戏版本"；当前 UMM 配置未启用版本点解析，因此不会阻止加载。
* `verboseLogging` 会记录全部按键事件，默认关闭。
