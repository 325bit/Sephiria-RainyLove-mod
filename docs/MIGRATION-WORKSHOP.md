# rainylove → Official Sephiria Steam Workshop: Migration Checklist

> **Implementation status (2026-11-14): Plan A is complete; `dotnet build -c Release` passes with 0 errors.**
> Entry point `Entry : WorkshopModBase`; configuration replaced with a self-contained JSON store (`ModConfig`); logging via `Log`; audio rewritten as WAV-only (removed ffmpeg / local HTTP server / UnityWebRequest / AudioClip reflection, keeping only pure C# WAV rendering + winmm `PlaySound`).
> Build output: `package/bin/RainyLove.dll`; `metadata.json` lives in `package/`. Outstanding runtime checks are listed in §4.
>
> **Corrections from real-world testing (evening of 2026-11-14)**
> 1. `multiplayer.installation` changed from `everyone` **to `local_only`** (the original call was wrong; see §2.9); version bumped to `1.4.1` accordingly.
> 2. Official DLL modules are **activated only when the game is started and a world scene is entered**: `OnModLoaded` does **not** run before that (including the title screen and the Workshop panel shown at launch), so pressing F7 there produces no UI. Once a world has been entered, `OnModLoaded` runs once and the code stays live even after returning to the title screen, so F7 keeps working. See §7 for log diagnosis.
> 3. A game with a `WorkshopLocal/` local package loaded **cannot join multiplayer** (stated explicitly in the official `publishing.md`); multiplayer testing requires uploading as Unlisted, subscribing, and removing the local folder.
> 4. Remove `.pdb` / `.deps.json` and other build artifacts before uploading (every file inside the folder is subject to review).
>
> **Upload validation, observed 2026-11-14 17:39**
> - In-game verification passed: after loading a save, the F7 panel works and both tracks play; the old bug where AIZO fired immediately on save load disappeared with the migration.
> - The upload dialog reported (Korean) `미리보기 이미지 필요: 패키지 루트에 thumbnail.png를 두거나 info.preview로 지정 (1MB 미만 png/jpg)`
>   = **a preview image is required**. Generated `thumbnail.png` from `Asset/牢师.png` by proportional downscaling (1024×470 / RGB PNG / 202KB, artwork untouched), placed it in the package root and declared it explicitly via `info.preview`; version bumped to **`1.4.2`** (editing metadata requires a version bump).
> - **Next obstacle: package size.** Code mods under review require the whole folder to be **≤20MB compressed**, and "every file in the folder is subject to review". The current `WorkshopLocal/rainylove/` holds two WAVs totalling **83.6MB** (48kHz / 16-bit / stereo; PCM barely compresses) and will exceed the limit.
>   → **Temporarily move `Asset/*.wav` out of the mod folder before uploading**, then put them back for local testing afterwards; subscribers must supply their own WAVs.
> - Before publishing: `thumbnail.png` (done), remove the WAVs (at upload time), remove `.pdb`/`.deps.json` (done).
> See the original checklist below (architecture table, review checklist, risks, directory layout).

> Goal: migrate rainylove from **BepInEx 5 + net472 (Mono)** to a **DLL module mod** (`WorkshopModBase`) supported by Sephiria's official Steam Workshop.
> Official repository / docs: https://github.com/team-horay/sephiria_modding
> Official API reference (baseline 1.1.0, incl. ModOptions / UI / HorayModAPI events): `docs/ko/api/` in that repository.

---

## 0. Feasibility (read first)

- **The game is a Mono build**: the install contains `Sephiria_Data/Managed/Assembly-CSharp.dll` and `MonoBleedingEdge`, and **no** IL2CPP `GameAssembly.dll`. The official DLL module spec states it works on Mono builds only → **fully feasible, no IL2CPP hurdle**.
- **rainylove is a pure client-side presentation mod** (per its README: reads game state only, never modifies server data, safe online). It did **not actually use Harmony** (`0Harmony` was referenced in the csproj but there is no `[HarmonyPatch]` / `Harmony.PatchAll` in the code) → the reference can be removed entirely, lowering risk.
- The real work is concentrated in three places: **entry point / lifecycle rewrite, configuration replacement, UI rewrite**; audio and state reading are largely reusable.
- **Two review-sensitive points must be handled**:
  1. `AudioLoader.cs` called `Process.Start(ffmpeg.exe)` in several places → the Workshop **forbids executing external code at runtime**; it triggers an "out-of-Workshop code execution" warning (requiring user consent) and ffmpeg cannot ship with the package. → **Remove the ffmpeg path and accept only a bundled WAV**.
  2. `AudioLoader.cs` plays via `[DllImport("winmm.dll")] PlaySound` → this is a system API (not loading an external exe/DLL), so it should be compliant in principle, but a P/Invoke into a native DLL may attract attention during decompilation review → **keep it and verify the review result; if flagged, fall back to Unity `AudioSource`** (see §4 / §6).

---

## 1. Current architecture vs. target architecture

| Dimension | Current (BepInEx 5 / net472) | Target (official DLL module / netstandard2.1) |
|---|---|---|
| Entry base class | `BepInPlugin` + `BaseUnityPlugin` | `WorkshopModBase` (or `HorayModBase`) |
| Entry method | `Awake()` | `OnModLoaded()` (called on world load) / `OnModUnloaded()` |
| Identifier | `[BepInPlugin("com.rainylove.lastsurvivor", ...)]` | `mod.modId` in `metadata.json` (e.g. `rainylove.lastsurvivor`) |
| Configuration | `Config.Bind` / `ConfigEntry<T>` / `Config.Save()` | See §3: Plan A custom JSON, or Plan B `ModOptions` |
| Logging | `BepInEx ManualLogSource` | `UnityEngine.Debug.Log/Warning/Error` (or an official logging API) |
| Resource paths | `BepInEx.Paths.PluginPath` + hard-coded `"RainyLove_AIZO"` | `Context.PackageRoot` (**never hard-code paths**) |
| Target framework | `net472` | `netstandard2.1` (C# 9, see csproj below) |
| Deploy location | `BepInEx/plugins/RainyLove_AIZO/` | `Sephiria/WorkshopLocal/<mod>/bin/*.dll` + assets |
| External loader | injected by BepInEx | BepInEx / MelonLoader and other external loaders are **forbidden** |
| Harmony | referenced but unused | remove the reference (if needed again, reference the game's built-in `0Harmony.dll` and **do not bundle it**) |

---

## 2. Required changes, by file

### 2.1 Project file `rainylove.csproj`
- `TargetFramework`: `net472` → **`netstandard2.1`**; set `<LangVersion>` to **`9.0`**.
- Point **all references at the game's `Sephiria_Data/Managed/` with `Private=false`** (never copy them into the package; duplicate assemblies conflict and inflate the review payload):
  - Keep: `Assembly-CSharp`, `UnityEngine.CoreModule`, `UnityEngine.IMGUIModule` (for `GUI`/`GUIStyle`/`Event`), `UnityEngine.TextRenderingModule`, `UnityEngine.InputLegacyModule` (if still needed).
  - **Remove**: `BepInEx`, `0Harmony` (Harmony is unused; delete it outright — if needed later, reference `$(GameDir)\Sephiria_Data\Managed\0Harmony.dll` with `Private=false`).
  - `Mirror` was initially removed, but **must be referenced**: `PlayerAvatar` derives from Mirror's `NetworkBehaviour`; without it the build fails with CS0012 / CS0311.
- Remove the `-p:DeployMod` BepInEx deployment target; output to `package/bin/` instead.
- Keep `AssemblyName` = `RainyLove`, `RootNamespace` = `RainyLove`.
- After the 2026-11-14 reorganisation, all sources live in `src/`, so `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>` plus an explicit `<Compile Include="src/**/*.cs" />` is used. This prevents a stray `.cs` under `docs/` or sample folders from being pulled into the mod assembly by the SDK's recursive default glob. `entryClass` depends on the namespace, not the folder, so this change is behaviour-neutral.

### 2.2 Entry class (old `Plugin.cs` → `src/Entry.cs`)
- Create/rework the entry, e.g. `public class Entry : WorkshopModBase`:
  ```csharp
  using UnityEngine;
  namespace RainyLove {
      public class Entry : WorkshopModBase {
          // Context is only valid from OnModLoaded onward; it is null in the constructor.
          protected override void OnModLoaded() {
              var go = new GameObject("RainyLove.Root");
              Object.DontDestroyOnLoad(go);
              Watcher   = go.AddComponent<LastSurvivorWatcher>();
              Aizo      = go.AddComponent<AizoWatcher>();
              go.AddComponent<AudioChannel>();
              go.AddComponent<ConfigUi>();
              Watcher.Initialize();
              Aizo.Initialize();
              // Configuration / UI initialisation (see §3)
          }
          protected override void OnModUnloaded() {
              // Unsubscribe and release resources (rainylove has no subscriptions; mainly stop playback).
              AudioChannel.Instance?.StopAll();
          }
      }
  }
  ```
- Delete the `[BepInPlugin(...)]` attribute; refactor every `Plugin.*` static (`Enabled`, `Volume`, `Log`, `Instance`, `PluginFolderName`, `ResolveAudioPath`, `MenuKey`):
  - Config values move into the configuration layer (§3), read through static accessors.
  - Logging becomes `Debug.Log` (wrap it in `static class Log { public static void Info(string s)=>Debug.Log("[rainylove] "+s); ... }`).
  - `ResolveAudioPath` becomes `Context.PackageRoot + "/Asset/" + fileName` (match the real directory's casing).
  - `Instance` / `Watcher` / `Aizo` become static fields on `Entry`.
- Keep the `RainyLove` namespace and the English-comment convention (project rule: source comments are in English).

### 2.3 Configuration system (BepInEx `Config` → see §3)
- Remove all `Config.Bind(...)`, `ConfigEntry<T>`, `AcceptableValueRange`, `SettingChanged`, `Config.Save()`.
- Rebuild using Plan A or B from §3:
  - **A (minimal changes)**: a self-contained JSON configuration (reference the game's bundled `Newtonsoft.Json.dll` with `Private=false`, do not bundle it), stored at `Context.PackageRoot + "/config.json"`; expose static accessors / `Save()`, and a change notification so volume edits re-render the WAV.
  - **B (official, more idiomatic)**: `ModOptions` / `ModOptionsRegistry` (see `api/ModOptions.md`) to register toggles / sliders / dropdowns in the game's options screen, saved per device. **Read `ModOptions.md` first** to confirm value types and change callbacks. ModOptions only offers simple controls, so **test-play/stop buttons and live status** still need a custom-drawn UI or `TryShowDialogue`.

### 2.4 Logging
- `Plugin.Log.LogInfo/Warning/Message` → `Debug.Log` / `Debug.LogWarning` / `Debug.LogError` (or an official logging API if one exists). A static `Log` wrapper minimises scattered edits.

### 2.5 Paths and assets
- Every `BepInEx.Paths.PluginPath` / `AppDomain.CurrentDomain.BaseDirectory` / hard-coded `RainyLove_AIZO` → **`Context.PackageRoot`**.
- Audio files `rainylove.wav` / `AIZO.wav` go under `WorkshopLocal/<mod>/Asset/`, supplied by the user (not committed, not packaged; documented in the README).
- Official warning: install paths differ per user, **never hard-code a path**.

### 2.6 UI `ConfigUi.cs` (custom IMGUI)
- A self-drawn `OnGUI()` panel is **in principle still usable** in the official Mono/Unity environment (IMGUI is a standard Unity mechanism). Dependencies on game-internal types such as `PlayerInputController.BlockAvatarInput` and `Cursor` capture need runtime verification.
- To confirm: whether the official environment permits mods to draw IMGUI inside the game loop; if restricted, switch to `OnTitleMenuReady` (menu button) + `TryShowDialogue` / `UIWidgetFactory` (see `api/UI.md`).
- `Config.Save()` becomes the §3 configuration layer's `Save`; `Plugin.MenuKey` becomes a read from that layer.
- Test-play/stop buttons keep calling `Watcher.TestPlay()/StopFromUi()` and `Aizo.TestPlay()/StopFromUi()`.

### 2.7 Input `InputBridge.cs`
- The reflection-based read of `Unity.InputSystem` (F7) **can be kept**: the game still ships `Unity.InputSystem.dll`, and the reflected indexer `Keyboard.current[Key.F7].wasPressedThisFrame` should still work.
- Changed: `TickSelfCheck` no longer takes a `BepInEx ManualLogSource`; it uses the `Log` wrapper. Also fixed a defect where `!Available` returned silently and left nothing in the log — resolution failures now emit an explicit warning.
- **Assembly-resolution hardening (evening of 2026-11-14)**: the official Workshop runtime resolves assemblies differently from BepInEx, so `Assembly.Load("Unity.InputSystem")` can fail. `EnsureResolved` now **first scans `AppDomain.CurrentDomain.GetAssemblies()` for an already-loaded `Unity.InputSystem`** and only falls back to `Assembly.Load`. The Input System is initialised at game boot, so this hits in practically all cases.
- Risk: if the game changes input handling or restricts reflection, switch to an official key API (if one exists) — verify at runtime first.

### 2.8 Audio `AudioLoader.cs` + `AudioChannel.cs` (main compliance work)
- **Remove every `Process.Start(ffmpeg...)` branch**: ffmpeg decoding only served the legacy fallback "WAV missing but an mp3 exists and ffmpeg is installed". New policy: **accept WAV only** (the user puts `rainylove.wav` / `AIZO.wav` in `Asset/`). Delete `ResolveFfmpegPath` and all 5 `Process.Start` sites (3 groups of `ffmpeg → WAV/PCM`) wholesale.
- Also removed as dead/high-risk code: the `TcpListener` local HTTP server, the `UnityWebRequest` path, and the `AudioClip`/`AudioSource` reflection. Playback had long since moved to winmm `PlaySound`, so none of it was reachable; removing it also shrinks the review surface (a network server inside a mod is a red flag).
- **Keep** the pure C# `RenderProcessedWav` (volume scaling) and **winmm `PlaySound`** (`[DllImport("winmm.dll")]`) — a system API call rather than loading an external DLL, so likely compliant; still **confirm it is not flagged in the review result after the first submission**.
- If winmm is flagged, or Unity's audio pipeline proves usable: the fallback is Unity `AudioSource`/`AudioClip` playback (cross-platform, most compliant), pending verification that Unity audio is no longer "broken" in the official environment.
- `Path.Combine(BepInEx.Paths.PluginPath, ...)` and similar → `Context.PackageRoot`.
- The `AudioChannel` arbitration logic (RainyLove vs AIZO priority, auto/manual playback, node-based stopping) is **kept in full**; only logging and path sources change. `StopAll()` was added for `OnModUnloaded`.

### 2.9 Game state reading (both Watchers + `PlayerHealth`)
- `FindObjectsOfType<PlayerAvatar>(true)`, `a.isInDungeon` / `a.IsDead` / `a.authority`: direct access to game types **still works through the `Assembly-CSharp` reference** in an official DLL module; keep.
- `PlayerHealth` reflection probe for health fields (candidate names `CurrentLife/Life/Hp/HP/Health`, etc.): Mono reflection is unchanged; keep (re-probe after a game update if field names change).
- Optional optimisation: use official events such as `HorayModAPI.OnPlayerAvatarSpawnedClientside` for incremental tracking instead of scanning every frame; the current per-frame scan works in the official environment, so keep it for now.
- `multiplayer.installation` is declared **`local_only`** (corrected after real-world feedback on 2026-11-14; the original `everyone` was a misjudgement).
  - The three values in the official `metadata.md`: `local_only` = "mods that affect only **my own screen / UI** (localization mods or simple UI mods)"; `everyone` = "**mods that add or change content**, all participants in the room must enable the same one"; `host_only` = "affects server-side computation only, no changes needed on participant clients".
  - rainylove registers no items/units/network objects, writes no game state, and produces no data requiring synchronisation. Each client **locally** reads `PlayerAvatar` state, plays audio locally, and draws IMGUI locally. Whether anyone else has it installed — or on the same version — cannot cause content divergence → semantically `local_only`; other participants need not download it.
  - Boundary of the official warning: `local_only`/`host_only` cause synchronisation failure if the mod actually touches "content that must also exist on participant clients (new item/unit registration, network objects, **code that must run on participant clients to maintain consistency**)" — that refers to content-bearing code which forks state. rainylove's client code is presentation-only with no state writes, so it does not fall into that category.
  - Caveat: `dll-mods.md` says "code mods are mostly `everyone`". That is a rule of thumb for code mods that register content, not a hard rule (the official `dll-sample` uses `everyone` because it changes player stats). If real-world testing shows the lobby rejecting `local_only` or misbehaving, fall back to `everyone` (at the cost of forcing other players to install it).

### 2.10 `metadata.json` and folder layout
- Create `metadata.json` (UTF-8):
  ```json
  {
    "info": {
      "title": "rainylove - Last Survivor",
      "description": "多人最后存活播雨爱 / 残血播AIZO（纯客户端表现）",
      "preview": "thumbnail.png"
    },
    "mod": {
      "schemaVersion": 1,
      "modId": "rainylove.lastsurvivor",
      "version": "1.4.2",
      "author": "rainylove",
      "multiplayer": { "installation": "local_only" },
      "modules": [
        { "type": "dll", "path": "bin/RainyLove.dll", "entryClass": "RainyLove.Entry" }
      ]
    },
    "tags": ["QoL"]
  }
  ```
  - `modId` must **never change once published**; every update **must** bump `version` (including metadata-only edits, e.g. `1.4.0 → 1.4.1 → 1.4.2`).
  - `entryClass` namespace + class name must match the code exactly (use `Outer+Inner` for nested classes).
  - Preview `thumbnail.png` (PNG/JPG, <1MB) is **mandatory for upload**; when missing, the dialog reports
    `미리보기 이미지 필요: 패키지 루트에 thumbnail.png를 두거나 info.preview로 지정 (1MB 미만 png/jpg)`.
    Generated from `Asset/牢师.png` by proportional downscaling (1024×470 / RGB PNG / 202KB, artwork unchanged), and declared explicitly via `info.preview`.
  - Do not use the `Library` tag (that is for runtimes/frameworks other mods depend on); a purely local presentation mod uses `QoL`.

---

## 3. Configuration approach (choose one)

**Plan A — minimal changes (recommended first)**
- Keep the F7 custom IMGUI panel; replace BepInEx `Config` with a self-contained JSON store (the game bundles `Newtonsoft.Json`; reference it without packaging).
- Pros: UI/interaction almost untouched, fast migration. Cons: not the "official options screen", but fully compliant.
- To implement: read/write `config.json`, plus a `SettingChanged` equivalent (volume change → call `Watcher.OnParamChanged()` / `Aizo.OnParamChanged()` to re-render).

**Plan B — official ModOptions (more idiomatic)**
- Use `ModOptionsRegistry` to register toggles / sliders / dropdowns in the game's options screen (master switches, volume, health thresholds, priority, etc.), saved per device.
- Complex panels (test-play/stop buttons, status display) can still use the F7 custom UI or `TryShowDialogue`.
- Pros: integrated with game settings, most compliant. Cons: must read `api/ModOptions.md` first for value/callback semantics, and rework ConfigUi's control bindings.

> Under either plan, non-pure-configuration features such as "test play / stop" and "live status" are best kept in a custom-drawn or official dialogue UI.

---

## 4. Review compliance checklist (verify before submitting)

**Must do**
- [ ] Remove every `Process.Start` / external exe call (ffmpeg) → bundled WAV only.
- [ ] Do not bundle DLLs the game already has (Unity*, Mirror, Newtonsoft.Json, 0Harmony) → reference with `Private=false`.
- [ ] No obfuscation, no native C/C++ DLLs; managed assemblies only.
- [ ] No generating / downloading / extracting any DLL at runtime (all DLLs shipped in plain form).
- [ ] Code mod folder **≤20MB compressed** (WAVs supplied by the user, not packaged).
  **Observed caveat**: for local testing the two WAVs sit in `WorkshopLocal/rainylove/Asset/` and total **83.6MB** (48kHz / 16-bit / stereo; PCM barely compresses) → **they must be moved out of the mod folder before uploading** or the limit is exceeded; put them back afterwards to continue local testing.
- [ ] `entryClass` and `path` in `metadata.json` match the real files exactly, including case.
- [ ] No hard-coded install paths; use `Context.PackageRoot` everywhere.
- [ ] Keep the generated `uploadticket.json` after upload (needed to keep updating the same Workshop item after a machine change / reinstall).
- [ ] Declare `multiplayer.installation: local_only` (pure client-side presentation; others need not install; see §2.9).
- [ ] Remove `.pdb` / `.deps.json` / build artifacts before upload (**every file in the folder is subject to review**).
- [x] Preview `thumbnail.png` (<1MB) — done: 1024×470 RGB PNG / 202KB / declared via `info.preview`.

**Verify at runtime**
- [ ] Whether the winmm `PlaySound` P/Invoke passes decompilation review (if flagged → switch to Unity `AudioSource`, after verifying Unity audio works in the official environment).
- [ ] Whether custom IMGUI `OnGUI()` is permitted; if not → `OnTitleMenuReady` + `TryShowDialogue` / `UIWidgetFactory`.
- [ ] Whether `InputBridge`'s reflection over InputSystem works in the official environment.
- [ ] Whether internal types such as `PlayerInputController.BlockAvatarInput` are accessible.

---

## 5. Risks and open items (by priority)

1. **DLL activation only on entering a world scene (observed)**: the official `OnModLoaded` does not run on the title screen, so no F7 UI in the title/Workshop panel is expected behaviour, not a bug. See §7 for log diagnosis. If configuration must be reachable from the title menu, move to the official `OnTitleMenuReady` + `TryShowDialogue` / `UIWidgetFactory` (Plan B territory).
2. **Whether `local_only` is accepted by the multiplayer lobby**: semantically correct (purely local presentation), but `dll-mods.md`'s rule of thumb is "code mods are mostly everyone". If the lobby rejects it or behaves oddly, fall back to `everyone` (cost: other players are forced to install).
3. **Experience loss from removing ffmpeg**: the "mp3 + ffmpeg auto-convert to WAV" convenience is gone; users must supply WAVs. Document this in the README / Workshop description — an accepted trade-off.
4. **winmm compliance**: the largest unknown, deciding whether system playback can be used. The Unity `AudioSource` fallback needs verification first.
5. **Whether IMGUI is permitted**: decides if ConfigUi can stay custom-drawn; otherwise move to the official UI.
6. **InputSystem reflection resolution**: the official runtime resolves assemblies differently from BepInEx; already changed to scan loaded assemblies first. If it still fails, F7 will not open but auto playback is unaffected (the log states the reason explicitly).
7. **Per-frame `FindObjectsOfType<PlayerAvatar>`**: functional but inelegant; could later be replaced with incremental event-based tracking. Non-blocking.
8. **netstandard2.1 compatibility**: a few `net472` APIs may need replacing (most of the `System.*` APIs rainylove uses are compatible).

---

## 6. Recommended layouts

### 6.1 Deployed mod folder (what the game loads)

```
Sephiria/
└── WorkshopLocal/
    └── rainylove/                  ← mod root (one folder = one mod)
        ├── metadata.json           ← required
        ├── thumbnail.png           ← preview image (<1MB)
        ├── bin/
        │   └── RainyLove.dll       ← build output (entryClass = RainyLove.Entry)
        ├── Asset/                  ← user-supplied rainylove.wav / AIZO.wav (not packaged)
        └── config.json             ← generated at runtime by Plan A (may ship as defaults)
```

### 6.2 Source repository (after the 2026-11-14 reorganisation)

```
Sephiria-RainyLove-mod/
├── rainylove.csproj                ← stays at root; OutputPath is relative to it
├── src/                            ← all 10 .cs sources (explicit <Compile Include="src/**/*.cs" />)
│   ├── Entry.cs                    ← WorkshopModBase entry point
│   ├── ModConfig.cs  Log.cs
│   ├── LastSurvivorWatcher.cs  AizoWatcher.cs
│   ├── AudioLoader.cs  AudioChannel.cs  PlaybackNodeScheduler.cs
│   └── ConfigUi.cs  InputBridge.cs
├── docs/
│   └── MIGRATION-WORKSHOP.md      ← this document (English)
├── package/                        ← build output + metadata + preview
│   ├── metadata.json
│   ├── thumbnail.png
│   └── bin/RainyLove.dll
├── package-upload/                 ← clean, WAV-free upload package (~250KB)
├── Asset/                          ← gitignored; local WAV/MP3/art, never uploaded
├── libs/  dll backup/              ← legacy references / old DLL backups
├── README.md  LICENSE  .gitignore
```

`entryClass` depends on the namespace, not the source folder, so moving sources into `src/` does not affect it.

Build: `dotnet build -c Release` (outputs to `package/bin/`). Then sync `package/metadata.json` + `package/thumbnail.png` + `package/bin/RainyLove.dll` into `Sephiria/WorkshopLocal/rainylove/` for local testing; **do not bring `.pdb` / `.deps.json`**.

---

## 7. Verification steps

1. `dotnet build -c Release` passes; sync the DLL, metadata and preview to `WorkshopLocal/rainylove/` (without `.pdb` / `.deps.json`).
2. Launch the game → the title screen's "Steam Workshop" panel → "My Mods" shows rainylove, marked "local only" / not requiring all participants.
3. **You must actually start a game and enter a world scene** (a scene where you control the character) before F7 produces a UI. In the title / Workshop panel `OnModLoaded` has not run, so the `ConfigUi` component does not exist and F7 doing nothing is normal framework behaviour.
   - Diagnose via `%LOCALAPPDATA%\..\LocalLow\TEAMHORAY\Sephiria\Player.log`:
     - `[WorkshopModRuntime] ✓ [Active] rainylove ...` and `[rainylove] v1.4.2 已加载` present → the DLL is active and F7 should work.
     - **Only `[Planned]`, with no `[Active]` / `활성화 결과`** → that run never entered a world scene (even other subscribed DLL mods would not activate); not a problem with this mod.
     - `[rainylove] 输入自检失败：...` present → InputSystem reflection resolution failed; F7 cannot open, but auto playback is unaffected.
4. Confirm `rainylove.wav` / `AIZO.wav` (16-bit PCM) are in `Asset/`; "audio ready" in the F7 panel means path resolution is fine. Trigger "last survivor" / "low health" to verify playback, then change volume to verify re-rendering.
5. **Multiplayer testing cannot use WorkshopLocal**: the official `publishing.md` states a game with local Workshop mods loaded cannot play multiplayer. Upload the mod as Unlisted, remove the local folder, and test with the subscribed copy.
6. Submit through the in-game "Upload to Workshop" button → wait for the review result and confirm no winmm / external-code warnings; under `local_only`, confirm other participants are not asked to install it.
7. After approval, subscribe yourself and switch to Public; every later update must bump `version` and be re-uploaded.
