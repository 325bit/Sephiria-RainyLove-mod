# rainylove → Official Sephiria Steam Workshop: Migration Checklist

> **Implementation status (2026-11-14): Plan A is complete; `dotnet build -c Release` passes with 0 errors.**
> Entry point `Entry : WorkshopModBase`; configuration replaced with a self-contained JSON store (`ModConfig`); logging via `Log`; audio rewritten to drop ffmpeg / local HTTP server / UnityWebRequest / AudioClip reflection. Playback now goes through **winmm MCI** (`mciSendString`) since 1.4.3 — see §8.
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
> - **Next obstacle: package size (RESOLVED in 1.4.3, see §8).** Code mods under review require the whole folder to be **≤20MB compressed**, and "every file in the folder is subject to review". The current `WorkshopLocal/rainylove/` holds two WAVs totalling **83.6MB** (48kHz / 16-bit / stereo; PCM barely compresses) and would exceed the limit.
>   → 1.4.3 ships the two source **MP3s** instead (10.1MB total, playback through winmm MCI). Subscribers no longer have to supply their own files; a same-named `.wav` still overrides the packaged `.mp3`. §8 records the decision and the measurements behind it.
> - Before publishing: `thumbnail.png` (done), remove `.pdb`/`.deps.json` (done). `package-upload/` is the clean upload tree: 10.30 MiB compressed, 51.5% of the limit.
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
        ├── Asset/                  ← shipped rainylove.mp3 / AIZO.mp3 (+ optional .wav overrides)
        ├── config.json             ← legacy location only; since §12 the live file is in
        │                              %USERPROFILE%\AppData\LocalLow\TEAMHORAY\Sephiria\rainylove\
        └── uploadticket.json       ← keep it, it carries publishedFileId (see §11)
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
4. Confirm `rainylove.mp3` / `AIZO.mp3` (or a same-named `.wav` override) are in `Asset/`; "audio ready" in the F7 panel means path resolution is fine. Trigger "last survivor" / "low health" to verify playback, then change volume to verify the system volume takes effect.
5. **Multiplayer testing cannot use WorkshopLocal**: the official `publishing.md` states a game with local Workshop mods loaded cannot play multiplayer. Upload the mod as Unlisted, remove the local folder, and test with the subscribed copy.
6. Submit through the in-game "Upload to Workshop" button → wait for the review result and confirm no winmm / external-code warnings; under `local_only`, confirm other participants are not asked to install it.
7. After approval, subscribe yourself and switch to Public; every later update must bump `version` and be re-uploaded.

---

## 8. Addendum (1.4.3): MP3 playback through winmm MCI

### 8.1 Why the audio pipeline changed again

PCM WAV cannot fit the 20MB limit: both tracks together are **79.68 MiB** at 48kHz/16-bit/stereo and ZIP level 9 only removes 1.9%. The only PCM shapes that fit are mono at ≤22.05kHz (18.30 MiB, 1.6MB of headroom) — an unacceptable quality drop for a music mod. The two source MP3s are already present and total **10.10 MiB**, so the question was only *how to decode them*.

Decoder candidates, measured on the real files (reference: ffmpeg decode, correlation):

| Candidate | Verdict |
|---|---|
| NLayer 1.16.0 (MIT, 72KB) | **Rejected.** Wrong output: correlation 0.066 (byte path) / −0.21 (float path), byte path even doubled the duration. |
| NVorbis 0.10.5 (MS-PL, 81KB) | Decodes correctly (0.994) and is fast (262× realtime), but the netstandard2.0 build hard-references `System.Memory`, which `Sephiria_Data/Managed` does not contain → dependency chain too risky. |
| MP3Sharp 1.0.4 (LGPL-3.0, 168KB, zero deps) | Decodes correctly (0.99999998 at a 2257-sample offset, i.e. the decoder's inherent delay) and was the runner-up, but adds a reviewed third-party binary plus a runtime temp-file render step. |
| **winmm MCI (`mciSendString`)** | **Chosen.** No bundled decoder, no temp files, no CPU cost; the OS decodes MP3. |

### 8.2 How it works

- `SystemAudio` now calls `mciSendString("open \"<path>\" type mpegvideo alias <alias>")`, then `setaudio <alias> volume to 0..1000`, then `play <alias>`. Stops use `stop` + `close`.
- **Single-device invariant.** MCI happily runs several aliases at once (verified: two MP3s reported `playing` simultaneously), and this mod must never sound two tracks together. `Play()` therefore always closes the previous device before opening the next one and tracks a single alias, so a second source can only *replace* the first. Verified: after `close`, `status <old alias> mode` fails with 263 (device no longer exists).
- `AudioChannel.Play` only updates its state when playback really started, so a device error can no longer leave the source "occupied but silent".
- **Volume:** the `mpegvideo` device accepts `setaudio volume` (verified: `status volume` round-trips 1000 → 0). The `waveaudio` device rejects it with 261 `MCIERR_UNSUPPORTED_FUNCTION`, so user-supplied WAVs keep the pure-C# render path that bakes the volume into a temp WAV. *(Superseded in 1.4.10: the WAV path was removed, §16.)*
- **Duration:** MCI's own length query is unusable for MP3 (measured 674907 ms for a 219370 ms file, with or without `set time format milliseconds`), so `Mp3Duration` walks the frame headers (ID3v2 skip, MPEG-1/2/2.5 Layer III, CBR + VBR). Measured 219.432 s / 215.856 s vs. 219.371 s / 215.787 s from ffprobe — the delta is one header frame, irrelevant at node granularity.

### 8.3 Measured MCI behaviour (Windows 10 22H2, 2026-11-14)

| Case | Result |
|---|---|
| Path with spaces / Chinese characters / 152-char path | `open` + `play` all return 0 — the historical 128-char MCI command limit does not apply here |
| Two aliases played at once | Both report `playing` → hence the close-before-open invariant |
| Start latency | ≈101 ms (`play`, then 1.5 s sleep, `position` = 1399 ms) |
| End of track | `status <alias> mode` → `stopped`; `play` again after `seek <alias> to start` works |
| Missing file | 275 `找不到指定的文件`, surfaced through `mciGetErrorString` in the log |
| Double `close` | 263 `设备未打开` — logged at info level, never thrown |

### 8.4 STA is mandatory for the MP3 driver — do not "simplify" this away

`open ... type mpegvideo` **only works on an STA thread**. On an MTA thread the MPEG video driver
(`mciqtz32.dll`, which builds a DirectShow graph internally) fails with **266 `MCIERR_CANNOT_LOAD_DRIVER`** —
a message that says nothing about apartments and looks exactly like "the system has no MP3 codec".

How this was isolated:

| Caller | Apartment | `open type mpegvideo` |
|---|---|---|
| Python (ctypes, uninitialised COM then STA) | STA | OK |
| .NET console app, main thread | MTA (CLR default) | **266** |
| .NET console app, `Thread.SetApartmentState(STA)` | STA | OK |
| .NET console app, `CoInitializeEx(STA)` called *inside* the thread | MTA (too late) | **266** (returns `RPC_E_CHANGED_MODE` 0x80010106) |

Two consequences baked into the code:

1. The apartment must be set **before** the thread starts (`Thread.SetApartmentState`). A managed thread is
   already MTA by the time its body runs, so calling `CoInitializeEx` inside the thread cannot fix it.
2. All MCI commands run on one dedicated STA worker thread (`RainyLove.MciAudio`, background, FIFO queue).
   This also removes the DirectShow device-open (~100 ms) from the game main thread, and it means the mod
   does **not** depend on whatever apartment Unity chose for its main thread.

If MP3 playback ever fails again with 266, check the log for
`播放线程当前是 MTA（RPC_E_CHANGED_MODE）` first — that is the apartment problem, not a missing codec.

### 8.5 Package layout (1.4.3)

```
package-upload/            10,842,484 B raw → 10,796,905 B compressed (51.5% of the 20MB limit)
├── Asset/  README.txt, rainylove.mp3 (5,399,255), AIZO.mp3 (5,189,521)
├── bin/    RainyLove.dll (45,568)
├── metadata.json (1.4.3, no `libraries` entry — nothing to declare any more)
└── thumbnail.png (206,700)
```

Known residual risk: playback depends on the OS MPEG/DirectShow components, which stripped Windows images (N/KN editions) may lack. A failure surfaces as `MCI 打开失败` in the log instead of silent breakage.

## 9. Addendum (1.4.5): local-player resolution and a switchable stop policy

### 9.1 `authority` is not "am I the local player" in this build

`NetworkBehaviour.authority` is overridden in the shipped Mirror build (`NetworkBehaviour:48-69`):

| Caller | `syncDirection == ServerToClient` (the default for `hp`, `IsDead`, `isInDungeon`) |
|---|---|
| host | `true` for **every** avatar |
| client | `false` for **every** avatar |

Consequences observed in multiplayer, both from the same one-line bug (`foreach (a) if (a.authority) return a`):

- **host**: the first avatar in `FindObjectsOfType` order wins — usually a teammate — so AIZO played on the
  host using a teammate's health.
- **client**: no avatar matches, `FindSelf()` returns null, `Evaluate()` returns on its first line, so AIZO never
  played on the client at all.

`PlayerLocator.GetLocal()` replaces it with an explicit fallback chain:

1. `NetworkClient.localPlayer` → `GetComponent<PlayerAvatar>()` (Mirror's own entry point, correct on host and client)
2. `NetworkBehaviour.isLocalPlayer` (what the game itself uses: `avatar.cs:3713`)
3. `NetworkBehaviour.isOwned`
4. the single avatar in the scene (offline play / not yet spawned)

Do not "simplify" this back to `authority`. `LastSurvivorWatcher` used it as well, for the
`OnlyWhenSelfIsLast` option: that option was permanently false on clients and permanently true on hosts.

### 9.2 Stop policy is now a switch: `ModConfig.DeferStop`

Both watchers share the flag (Main Settings tab, `顺延播放（到时间轴节点再停）`):

| | condition ends → behaviour | worst case |
|---|---|---|
| `false` (default) | hard stop on the next scan (≤ `ScanInterval`, 0.25 s) | track length as heard = time until the condition ended |
| `true` | defer to the next timeline node (`PlaybackNodeScheduler`) | **the whole track** (219 s for RainyLove) |

The option is labelled 顺延播放, not 顺延停止: it is described by what it adds (a delayed playback tail) rather than by
what it postpones, because 顺延停止 read as "delay the stop" and players could not tell whether the switch meant
"stop later" or "do not stop". The C# field keeps the name `DeferStop` -- it doubles as the `config.json` key, and
renaming it would silently reset the setting for every existing user.

The deferral behaviour is what players reported as "the music never stops", and it is reachable in three ways,
all measured with the real scheduler in a frame-by-frame simulator:

| condition ends at | stop point |
|---|---|
| 10 s | 87.1 s (p2) |
| 90 s | 130.07 s (p3) — 43 s of unwanted tail |
| 131 s | end of track, 219.4 s |
| re-trigger inside the p2 window, then ends again | 130.07 s |
| re-trigger inside the p2 **and** p3 windows | end of track, 219.4 s |

That is why `DeferStop` defaults to `false`. Turn it on only when the musical tail is wanted more than
predictability.

### 9.3 `StartAuto` is idempotent for the already-playing source

`AudioChannel.StartAuto` now returns `true` without calling `Play()` when the same source is already auto-playing.
Before this, any upper-layer re-evaluation (the `_playedThisRun` / `_triggered` flag being cleared while the music
was still sounding) re-entered `Play()` → `SystemAudio.Play()` → `CloseDevice()` + `open`, which seeks back to 0:00
(measured: 3 s in, position 2908 ms; after close+reopen, 720 ms at 0.8 s). The track then never reaches its end.
`LastSurvivorWatcher.AutoPlay()` also returns `bool` now, so the flag is latched only when playback really started
(previously a request rejected by the priority arbitration still marked the run as "already played").

Out of scope for 1.4.5, deliberately: `AizoWatcher.Evaluate` still returns early when the local player's hp reads
`<= 0` (death while AIZO plays keeps it sounding to the end), short user-supplied audio (<131 s) skips every node,
and a zero-length duration leaves `_endTime == 0` so the natural-end timer never fires. None of these can happen
with the packaged MP3s; all three only matter when a player drops their own short/odd file into `Asset/`.

## 10. Addendum (1.4.6): a song must not outlive the run

Three behaviour fixes reported from multiplayer play. No new dependencies, no change to the manifest.

### 10.1 Leaving the world stops the sound (`AudioChannel.OnActiveSceneChanged`)

The sound is played by `mciSendString`, i.e. by a Windows device that lives outside Unity's scene graph. Neither
scene unload nor `DontDestroyOnLoad` teardown closes it, so a track started inside a dungeon kept sounding after
the player returned to the title screen or the lobby. The only previous stop paths were `OnModUnloaded` (not raised
on that transition) and the natural-end timer.

`AudioChannel` now subscribes to `SceneManager.activeSceneChanged` and calls `StopAll(...)` when anything is still
sounding. Entering a world fires the same event, but nothing is playing at that moment, hence the early return.
Both watchers get `ResetRunState()` so the next world does not inherit the previous run's flags.

### 10.2 An empty dungeon ends the run (`LastSurvivorWatcher.Evaluate`)

`inDungeonTotal == 0` used to feed `false` into the node scheduler, i.e. it *scheduled* a stop instead of ending the
run. With deferral on, that plan could outlive the dungeon entirely - the exact shape of the report "it never
stops". It now calls `LeaveRun`, which drops the plan, silences the channel (both sources, manual playback
included) and clears the run state, so re-entering can trigger again.

Debounce: the empty condition must persist `EmptyGraceSec` (0.5 s, i.e. two scans) before it is acted on. A level
or scene transition reports zero players for a frame or two, and acting on the first frame would cut the track
during normal play. Two independent timers are used - `_emptySince` for "nobody inside the dungeon" (avatars still
exist, everyone is in the lobby) and `_noAvatarSince` for "no avatar at all" (title screen, world teardown) - so the
two conditions cannot reset each other's debounce.

### 10.3 Stop logs name the track

`SystemAudio.Stop(null)` used to produce `系统音频已中断：` with an empty tail, which made the log useless for
telling the two songs apart. Every stop path now passes `_currentWav`, and `AudioChannel.StopAll` additionally logs
`停止播放（<source>）：<reason>`.

### 10.4 Verified with a harness that compiles the real sources

`D:/tmp/rlworld` compiles the unmodified `src/AudioChannel.cs`, `src/LastSurvivorWatcher.cs` and
`src/PlaybackNodeScheduler.cs` against stubs, and drives `Evaluate()` directly: 25 checks, all pass, covering the
scene-change stop, the 0.5 s debounce (0.0 s / 0.3 s / 0.6 s), plan cancellation on leaving, re-trigger in a new
run, manual playback being stopped on leave, and the no-avatar path. The 1.4.5 idempotence suite (`D:/tmp/gtest`)
still passes.

Note for future work: `WorkshopVerification.IsGateActive()` returns false because `ModGuardWorkshop.EnforceGate`
has no assignment anywhere in `Assembly-CSharp.dll`. That is why an unreviewed DLL dropped into
`workshop/content/2436940/<id>/bin` technically still loads. **Do not use that as a deployment shortcut** — see §11.

---

## 11. Deployment target: `WorkshopLocal/`, never `workshop/content/`

Confirmed the hard way on 2026-11-15 by overwriting the subscribed copy instead of the local package.

**Wrong (what was done once):**

```
F:\SteamLibrary\steamapps\workshop\content\2436940\3839531454\bin\RainyLove.dll   ← Steam's download of the
                                                                                   approved build. Do not write here.
```

Putting a newer DLL in that folder makes the client-side review gate complain, because the folder no longer matches
the approved manifest for item `3839531454`:

| UI text | Meaning |
|---|---|
| `这是无法加载认证信息的模组` | the manifest/hash pair for the item could not be satisfied by the files on disk |
| `没有当前文件的审核提交记录` | the DLL hash on disk matches no submission record |
| `模组的DLL与创意工坊版本不同` (`审核失败原因`) | the DLL differs from the Workshop version |

The in-game panel also shows `version 1.4.6` for a subscriber-visible entry that is still 1.4.5 online, which is a
second symptom of the same mistake. Under `local_only` this is only cosmetic (the gate logs a warning and still
runs the code), but it is wrong and it makes the panel untrustworthy for diagnosing the real mod.

**Right:**

```
F:\SteamLibrary\steamapps\common\Sephiria\WorkshopLocal\rainylove\
├── bin\RainyLove.dll      ← build output (no .pdb, no .deps.json)
├── metadata.json          ← version bumped
├── thumbnail.png          ← preview (info.preview)
├── Asset\                 ← rainylove.mp3 / AIZO.mp3 / README.txt
├── config.json            ← local test settings, not uploaded
└── uploadticket.json      ← MUST be preserved, see below
```

The user uploads from the in-game Workshop panel ("我的模组" → "上传到创意工坊"). Do not upload on their behalf.

Two rules that keep the update going to the *same* item:

1. **Keep `uploadticket.json`.** `WorkshopUploadPreparer` sets `plan.PublishedFileId = plan.Ticket?.PublishedFileId`,
   so deleting the ticket loses the target id and `PublishToSteam` falls through to `CreateItem` — a brand new
   Workshop entry instead of an update. Its stored `version` / `contentHash` are just a record of the last
   submission; leave them alone.
2. **Bump `metadata.json` `version`.** `alreadyPublished` in `WorkshopUploader` requires
   `PublishedContentHash == plan.ContentHash`; a changed DLL changes the hash, so the upload correctly takes the
   `PublishToSteam` → `SubmitItemUpdate` path for `3839531454`. Bumping the version keeps the ticket readable.

Restoring the subscribed folder to the published build (needed after the mistake above) is a plain file copy from a
backup of the released DLL + `metadata.json`; a Steam "verify files" also works.

---

## 12. Settings live in `Application.persistentDataPath`, not next to the DLL

Through 1.4.6 `ModConfig` wrote `config.json` into `Context.PackageRoot`. That breaks for real subscribers: the
`workshop/content/2436940/<id>/` folder is re-downloaded on every update, so settings were lost (or, if the folder
was read-only, silently dropped), and the file sat in a directory the user is not expected to touch. Every other
Sephiria mod keeps its settings under `Application.persistentDataPath`, so this one now does too:

```
%USERPROFILE%\AppData\LocalLow\TEAMHORAY\Sephiria\rainylove\config.json
```

`ModConfig.ConfigPath` resolves it once (the API must be called on the Unity main thread; `Load()`/`Save()` only run
from `OnModLoaded` / the F7 panel, both main-thread). Two behaviours matter:

1. **Migration.** If `PackageRoot\config.json` exists and the per-user file does not, `Load()` adopts the old values,
   writes them to the new path, then renames the old file to `config.json.bak`. The order is `_settings = loaded;
   SyncFromSettings(); Save();` — publishing to the static fields *before* `Save()` is what stops `SyncToSettings()`
   from writing the defaults back over the values just read.
2. **Precedence.** If both files exist, the per-user one wins and the legacy file is left untouched.

Fallback: if `persistentDataPath` is empty or throws, the path degrades to `PackageRoot\config.json` (the old
behaviour) instead of losing the settings. `PackageRoot` is still needed either way — it is how the shipped
`Asset/rainylove.mp3` / `Asset/AIZO.mp3` are located.

Verified by `D:/tmp/cfgtest`, which compiles the unmodified `src/ModConfig.cs` against stubs for `Application` and
`Log`, with the real `Newtonsoft.Json` from `Sephiria_Data/Managed`: 26 checks covering a fresh install, legacy
migration (including that `DeferStop: true` survives), save/load round-trip, the fallback path, both-files-present
precedence, and the literal target path above.

---

## 13. Addendum (1.4.7): AIZO keeps playing through a mini-boss / boss fight

New AIZO option `ModConfig.AizoBossHold` (panel label 「BOSS 战期间保持播放（战后按顺延设置停止）」, **off by default**).
While a Miniboss or Boss is alive, AIZO must not stop for any reason, and a track that reaches its end repeats;
once the fight is over the existing `DeferStop` policy applies again (immediate stop, or defer to the next timeline
node). Starting still needs the health trigger — the hold only extends playback, it never forces a start.

### 13.1 Why the detector does not use `BossSpawner.IsBossBattleInProgress`

That property looks perfect and is a trap:

```csharp
public bool IsBossBattleInProgress => battlePhase == EBattlePhase.BeginBattle;   // BossSpawner.cs:202
```

`battlePhase` is only written inside `BossSpawner.Update()` under `if (base.isServer)`, and the client-side RPC body
is just `ClientOpenBossHPBar()` (`BossSpawner.cs:1537`). It is therefore true on the host and **permanently false on
every client** — using it would repeat the 1.4.5 failure where AIZO only reacted on the host (§9.1).

What *is* replicated: `UnitAvatar.monsterType` and `UnitAvatar.IsDead` are both `[SyncVar]`. `EMonsterType` is
exactly `{ Normal, Miniboss, Boss, Dummy }`, so:

> a fight is ongoing while at least one living `UnitAvatar` has `monsterType == Miniboss || monsterType == Boss`.

Both kinds are spawned by the room the player entered and lock the player inside via the spawner's `playerPrevent`
SyncVar, so a living boss cannot belong to some other room. `Dummy` (training target) is excluded.

### 13.2 Hysteresis: `BossFightLogic`

`src/BossFightLogic.cs` holds the decision with no Unity types (so a console harness can drive it):

| Transition | Rule |
|---|---|
| boss appears | `Active` becomes true **immediately** |
| boss disappears | `Active` stays true for `EndGraceSec = 1.5s` of continuous absence, then goes false |
| boss reappears inside the grace window | countdown resets, `Active` never flickers |

The grace exists because the raw signal drops out between summon waves and the instant the boss dies — the victory
cut-scene and rewards are still running then. Ending the hold on that first false frame would cut the music in the
middle of the death animation. `BossFightWatcher` scans `Object.FindObjectsOfType<UnitAvatar>()` every 0.5s
(`ScanSec`) and is driven from `AizoWatcher.Update`.

### 13.3 Loop: MCI `repeat`, measured

`SystemAudio.PlayOnWorker` now issues `play <alias> repeat` when a loop is requested. Measured on the real AIZO.mp3
(mpegvideo), repeating the first 3 seconds:

```
play rep1 repeat from 0 to 3000 -> ok
    t=1.0s pos=906ms   t=3.0s pos=1338ms   t=5.0s pos=160ms   mode=playing throughout
stop -> ok
```

The position wraps inside the range and the device keeps playing, so the loop is gapless — no close/open, unlike
restarting from the mod. `waveaudio` rejects the flag with **259 / MCIERR_UNSUPPORTED_FUNCTION**, so a WAV (still
reachable through the legacy `.wav` override at the time) falls back to a single-shot playback plus a warning; only MP3 loops.
This is one more reason the WAV path was retired in 1.4.10 (§16).

### 13.4 Timeline nodes inside a loop

While looping, `Time.unscaledTime - _startTime` runs past the track length forever, so `AudioChannel.GetElapsedSeconds`
asks the device instead: `set <alias> time format milliseconds` at open, then `status <alias> position`, cached for
0.1s (a per-frame MCI round-trip is not free, and `TickScheduledStop` runs every frame while a plan exists). The
returned value is the position **inside the current repetition**, which is exactly what `PlaybackNodeScheduler`
needs — that class is unchanged.

### 13.5 State machine

`hold = AizoBossHold && BossFightWatcher.Active`, plus a latch (`_holdLatch`) that records "this fight is why the
music is on":

| Situation | Result |
|---|---|
| hold, auto-playing | no stop at all; node schedule cleared every scan |
| hold, not playing, latch set (track ended) | restart with loop |
| hold, not playing, no latch, health low | normal trigger applies (starts with loop) |
| hold, not playing, no latch, health fine | stays silent — the hold never forces a start |
| hold ends, health fine, `DeferStop` off | stop immediately |
| hold ends, health fine, `DeferStop` on | defer to the next node, computed from the in-loop position |
| hold ends, health still low | keep playing (the trigger condition still holds) |
| UI 「音频播放停止」 or a volume change | latch cleared, so the hold cannot resurrect it |
| scene change | `StopAll` + `ResetRunState` (which also calls `BossFightWatcher.Reset`) |

One deliberate gap: a track that was already sounding *before* the fight cannot be converted to looping in place
(MCI has no "add repeat to a running device"), so that first repetition plays to its end and only the restart is
looping. Restarting it on fight start would cut the song at an arbitrary point, which is worse.

### 13.6 Verification

- `D:/tmp/bossfight` — 41 checks on `BossFightLogic` + `BossFightWatcher`: every `EMonsterType`, dead-corpse
  filtering, null entries, the 1.5s grace, wave-gap flicker, the 0.5s scan throttle, full timelines, and the
  injectable `Source` seam.
- `D:/tmp/aizohold` — 36 checks compiling the unmodified `AizoWatcher` + `AudioChannel` + `PlaybackNodeScheduler`:
  the eleven rows of §13.5, including "replay after the track ends", "started before the fight", "manual stop is
  not resurrected" and "no forced start at full health".
- `D:/tmp/rlworld` — 35 checks (the 25 from 1.4.6 plus 10 for the loop plumbing: no self-extinguishing end timer,
  device-sourced position, modulo fallback, driver-rejected loop).
- `D:/tmp/gtest` — 19 checks, unchanged.

## 14. Fix (1.4.8): the boss hold never released

1.4.7 shipped the hold with a detector that answered the wrong question. `AnyBossAlive()` asked "does any
living `UnitAvatar` with `monsterType` Miniboss/Boss exist in the loaded scene", not "is the player fighting
one". Dungeon floors pre-spawn their rooms, so a boss waiting in a room the player has not entered yet
already satisfied it; combined with the fact that nothing reset that state at a run or floor boundary, the
hold engaged on save load and stayed engaged for the whole run, which made every stop path in
`AizoWatcher.Evaluate` unreachable. Reported symptom: after killing a boss, HP back above the threshold,
AIZO still would not stop while clearing normal rooms.

`Player.log` for 1.4.7 proves it without any new instrumentation: exactly one `检测到小BOSS/BOSS` line
(taken at save load, one frame *before* `avatar=new`) and exactly one `BOSS 战结束` line (~19 minutes and
11 floors later), while the loop start at 1256 requires `BossFightWatcher.Active == true` at that moment.
Any intermediate false→true transition would have logged, so `Active` was true continuously.

### 14.1 New rule

`BossFightLogic.IsBossEngaged(isBossType, isDead, inBattle, sqrDistance, engageRadiusSqr)`:

```
isBossType && !isDead && (inBattle || distance <= 12)
```

- `inBattle` is `UnitAvatar.IsInBattle`, a SyncVar. `UnitAI_NewBasic.OnFoundTarget` calls
  `Avatar.StartBattle()` as soon as the AI has a hostile target, and `UnitAI_BossBasic : UnitAI_NewBasic`
  does not override `OnFoundTarget`/`OnLostTarget`, so this is the engine's own "this boss is fighting"
  flag and it is visible on clients (unlike `BossSpawner.IsBossBattleInProgress`, which is host-only).
- `12` is copied from the game: `UnitAI_NewBasic` treats `distance > 12` as "the target walked into another
  room, stop chasing", so 12 is its room radius. It only covers the entry window, where the boss is already
  spawned in the room but its AI has not picked a target yet.
- A local player must exist, so the world-load window (before the first `PlayerAvatar` spawns) can no
  longer report a fight.

### 14.2 Boundary resets

- `LastSurvivorWatcher.LeaveRun()` now also calls `Entry.Aizo?.ResetRunState()`. It used to reset only its
  own component, so `AizoWatcher._holdLatch` and `BossFightWatcher` survived "left the dungeon" and
  "nobody is in the run". `AudioChannel.OnActiveSceneChanged` already reset both on a world swap; this
  covers the two paths that never swap the scene.
- `AizoWatcher.Evaluate()` clears `_holdLatch` / `_holdLogged` when `FindSelf()` returns null, because a
  missing local avatar means no fight can be keeping the track alive.

### 14.3 Detection log carries its own evidence

The enter transition now logs `unit=<name> type=<type> inBattle=<bool> dist=<distance>`. This is permanent,
not a temporary probe: if the detector ever misfires again, the log names the offending unit.

### 14.4 Verification

- `D:/tmp/bossfight` grew from 41 to 61 checks: the engagement truth table, 1.4.7 regressions (boss 40 units
  away in an unentered room, boss 13 units away in the next room -> not engaged), the entry window
  (11.9 units, no target -> engaged), distance-independent `inBattle`, dead/dummy/normal filtering, "no
  local player -> never engaged", and `Reset` interrupting an active hold.
- `docs/DEBUG-aizo-boss-hold.md` holds the full Chinese debug report: causal chain, hypothesis table with
  exclusions, and the acceptance protocol to read on the next run.

## 15. Fix (1.4.9): the player avatar is itself a `Boss`

§14's diagnosis was incomplete. Excluding pre-spawned rooms and requiring a local player was not enough,
because the local player *was* the match. 1.4.8's detector log named it on the user's very next run:

```
检测到小BOSS/BOSS → 战斗期间不停止播放，播完自动重播。
   （unit=PlayerAvatar [connId=0] type=Boss inBattle=False dist=0.0）
```

`PlayerAvatar : UnitAvatar` (`PlayerAvatar.cs:11`), and `monsterType` is a **prefab-serialized field** - no
code in the assembly ever assigns `EMonsterType.Boss`, so the value comes from the player prefab itself.
The player therefore satisfied "a living Miniboss/Boss unit exists" at distance 0 for the entire run, the
hold latched on spawn and never released, and every stop path in `AizoWatcher.Evaluate` stayed unreachable.

The game guards the same way: `UI_EnemyViewer.cs:112` does
`if (!(obj is PlayerAvatar) && obj.monsterType == EMonsterType.Normal)` before trusting `monsterType`.

### 15.1 Rule change

`BossFightWatcher.AnyBossEngaged()` now skips, in this order:

1. `u is PlayerAvatar` - the local player and every teammate;
2. `u.NetworkLeader is PlayerAvatar` - player-owned followers / summons, which are on the player's side
   and can legitimately carry a Miniboss `monsterType`;
3. anything whose `monsterType` is not Miniboss/Boss, or that is dead.

Everything else from §14.1 still applies: `inBattle || distance <= 12`, and a local player must exist.

### 15.2 Why the 1.4.8 test suite missed it

The harness stub declared `PlayerAvatar` as a standalone class that never appeared in the `UnitAvatar` scan
list, so the one shape that mattered could not be expressed. The stub now mirrors the real inheritance
(`PlayerAvatar : UnitAvatar`) and `bossfight` grew from 61 to 69 checks, including a dedicated group:
player with `monsterType=Boss` at distance 0 -> not engaged, player in battle -> not engaged, teammate ->
not engaged, player follower with `monsterType=Miniboss` -> not engaged, real boss -> engaged and reported
by name.

### 15.3 Lesson recorded in the memory file

Before trusting `monsterType` on any `UnitAvatar`, exclude `PlayerAvatar` (and player-owned units). This is
the same filter the game itself uses.

## 16. Removed (1.4.10): the WAV path is gone

The WAV branch outlived its purpose. It existed because the first Workshop build could only ship a raw PCM file the
user dropped into `Asset/`, and it was kept afterwards as a "lossless override" that took precedence over the
packaged MP3. By 1.4.10 both reasons were dead: the package ships its own MP3s, and the one feature the mod cannot
work without - the boss-fight hold - needs `play ... repeat`, which the `waveaudio` device rejects with 259. A WAV
would have silently degraded the hold to a single-shot playback. Retired rather than documented as a caveat.

### 16.1 What was deleted

| File | Removed |
|---|---|
| `AudioLoader.cs` | `RenderProcessedWav` (pure-C# 16-bit PCM volume scaler), its `_renderCache` / `_bySource` caches, `DeleteCacheFile` (temporary files in `%TEMP%`), `GetWavDuration`, the RIFF/WAVE parsing, and the `waveaudio` branch in `SystemAudio.PlayOnWorker` |
| `ModConfig.cs` | `AudioExtensions` is now `{ ".mp3" }` (it was `{ ".wav", ".mp3" }`, WAV winning), and the dead `ResampleHz` field is gone from both the static surface and the JSON DTO |
| `AizoWatcher.cs` / `LastSurvivorWatcher.cs` | `_volumeBaked` (the "was the volume baked into a temp WAV, so a volume change must reload" flag) and the re-render branch in `OnParamChanged` |
| `AudioChannel.cs` | `_currentWav` renamed `_currentPath` - the field never held a WAV, but the name lied |
| `ConfigUi.cs` | "未渲染（首次/改参数后自动渲染）" -> "未就绪（缺 MP3 或格式不符）"; the boss-hold hint no longer says "需要 MP3，WAV 不支持循环" |
| `Asset/README.txt` (both packages) | Rewritten: MP3 only, WAV files are ignored |

`TryPrepareAudio` keeps its signature but now only validates: it returns the source path untouched and the clamped
volume, and returns false for anything that is not an existing `.mp3`. `SystemAudio.PlayOnWorker` re-checks the
extension and hard-codes `type mpegvideo`, so a stray `.wav` cannot reach the device even if a caller bypasses
preparation.

### 16.2 What was kept on purpose

- **The `play ... repeat` retry** (loop requested, driver refuses, play once anyway). That branch was originally
  written for `waveaudio`, but it is still the right behaviour if `mpegvideo` ever refuses the flag: a boss fight
  should not go silent because of a driver quirk.
- **`Mp3Duration`'s frame walker.** MCI's own length query is wrong for these files (§8.2), so the duration still
  has to come from the file.
- **`ResampleHz` in existing `config.json` files.** The key is simply ignored now (Newtonsoft skips unknown members),
  and it is no longer written back on the next `Save()`, so the field disappears from user files without a migration
  step.

### 16.3 Verification

- New harness `D:/tmp/aloader` (20 checks) compiles the real `AudioLoader.cs` and asserts: `IsMp3` is
  case-insensitive and rejects `null`/`.wav`, `TryPrepareAudio` refuses a missing file and an existing `.wav` (while
  still returning the source path untouched for `.mp3`), the volume is clamped to 0~1, `GetAudioDuration` returns 0
  for `.wav` and for a fake MP3 but 215.86 s for the shipped `AIZO.mp3`, and `SystemAudio.Play` returns false for a
  WAV.
- `D:/tmp/cfgtest` grew from 26 to 30: `AudioExtensions` is `{ ".mp3" }`, a directory holding only `AIZO.wav`
  resolves to "not found", and when both `AIZO.wav` and `AIZO.mp3` exist the MP3 is selected. The legacy-config
  fixture still contains `ResampleHz` and now also asserts that the key is not written back.
- `gtest` 19 / `rlworld` 35 / `aizohold` 36 / `bossfight` 69 unchanged and green. Total 199 checks.
- `package/bin/RainyLove.dll` deployed to `WorkshopLocal/rainylove/`, `metadata.json` bumped to **1.4.10**,
  `package-upload/` synced (DLL + metadata + Asset/README.txt).

### 16.4 Note for the subscriber-facing description

"The mod plays MP3 only" was already true in practice for everyone who never dropped a WAV into `Asset/` - which is
every subscriber, because `Asset/` is not reachable for them. This change only affects the author's local testing
folder, where `rainylove.wav` / `AIZO.wav` used to shadow the MP3. Those files can be deleted; they are ignored now.

---

## 17. Addendum (1.4.12): the boss hold also covers RainyLove

`LastSurvivorWatcher` gets the option AIZO already had: `ModConfig.RainyLoveBossHold`, panel label
「BOSS 战期间保持播放」 on the RainyLove tab, **off by default**, JSON key `RainyLoveBossHold`.

The contract is the one from §13, applied to the other track:

- While `BossFightWatcher` reports a fight, **no stop path is reachable** — the last-survivor condition,
  `DeferStop` and the node schedule are all ignored — and the track is requested with device-level MCI `repeat`,
  so reaching its end does not silence it.
- When the fight ends the normal policy takes over: immediate stop with `DeferStop` off, or "finish this
  verse/chorus" with it on. Only extends, never starts: RainyLove still begins solely on the last-survivor
  trigger, so turning the hold on cannot make the song play in single player.

### 17.1 Code shape (`src/LastSurvivorWatcher.cs`)

- `_holdLatch` / `_holdLogged`, cleared in `ResetRunState`, `StopFromUi` and `OnParamChanged` — the same
  "the user silenced it, do not resurrect it" rule as AIZO.
- The hold block sits at the top of `ScanCondition`, before the `DeferStop` branch, so it short-circuits both the
  immediate-stop and the node-deferral paths. `TickScheduledStop` bails out while `_holdLatch` is set: the plan is
  cleared on the next 0.25s scan, but a node landing inside that gap must not cut the track.
- `AutoPlay()` passes `loop = RainyLoveBossHold && BossFightWatcher.Active`, including the `_playAfterLoad` path.
  A run-through that was already playing when the fight started stays non-looping until it ends (MCI cannot add
  `repeat` to a running device); the replay the hold triggers afterwards is looping. Same accepted trade-off
  as AIZO (§14.2).
- `BossFightWatcher.Tick()` is now called from `LastSurvivorWatcher.Update` as well. Previously only `AizoWatcher`
  ticked it, and its early return on `!AizoEnabled` meant that switching AIZO off would freeze the detector the
  RainyLove hold depends on. `Tick` throttles itself to one scan per 0.5s, so the duplicate call per frame costs
  one comparison.
- The detector's transition logs moved from `[rainylove][AIZO]` to `[rainylove][BOSS]`; they are no longer
  AIZO-specific.

### 17.2 Verification

`D:/tmp/rlworld` grew from 43 to 68 checks with a ⑤ group driven through the real watcher and the real
`AudioChannel`: a start before the fight does not request `repeat`; a revived teammate no longer silences the
song while a boss is engaged; the non-looping run-through resets at the track end and the hold replays it with
`repeat`; the fight ending releases the latch and (with `DeferStop` off) stops immediately; the hold alone never
starts playback; `StopFromUi` is not undone by the hold; `ResetRunState` clears the latch. All six harnesses
green: gtest 21 / rlworld 68 / bossfight 69 / aizohold 44 / aloader 20 / cfgtest 30.

### 17.3 Panel wording

Same style the AIZO tab already uses, so the two tabs read as one option: label 「BOSS 战期间保持播放」,
on 「已开启：小BOSS/BOSS 存活期间不再停止，播完自动重播，直到当前BOSS战结束」, off
「已关闭：只看存活人数决定何时停止」 (AIZO keeps 「已关闭：只看血量阈值决定何时停止」).
The label and the 'already on' line are shared constants in `ConfigUi.cs`; only the 'off' line names the trigger
that still decides on its own.
