# com.unity.pipeline: Unity 2022.3 backport

This is Unity's `com.unity.pipeline` package (0.8.0-exp.1), patched so it runs on **Unity 2022.3**. Officially the package needs Unity 6.0 or newer. The main reason for the backport is VRChat, which requires 2022.3.22f1. With the package installed, the [Unity CLI](https://unity.com) can drive a running 2022.3 Editor (`unity status`, `unity command eval`, scene, GameObject and asset commands, and more).

The first commit is the upstream package, unmodified. The second commit holds all of the backport changes, so `git diff HEAD~1` shows exactly what was changed.

## Changes

All changes apply only to Editors older than the version that added each API, so the package behaves as before on Unity 6.

| File | Change |
|---|---|
| `Runtime/Plugins/CodeAnalysis/*.dll.meta` | Rewritten in the 2022 PluginImporter format. Unity 2022 silently ignores the Unity 6 format, so the bundled Roslyn and System.Reflection.Metadata DLLs never reached the compiler. |
| `Editor/PipelineAnalytics.cs` | Disabled below 2023.2 (`IAnalytic` doesn't exist there) and replaced with no-op methods. |
| `Editor/Commands/Assets/AssetCommands.cs` | Uses `PhysicMaterial`, the name before the 2023.1 rename. |
| `Editor/Commands/Materials/MaterialCommands.cs` | Reads `m_CustomRenderQueue`, because `Material.rawRenderQueue` isn't public before 2023. |
| `Editor/Console/EditorConsoleGroundTruth.cs` | Gets console counts via reflection on the internal `LogEntries` class, because `ConsoleWindowUtility` isn't public in 2022.3. |
| `Runtime/Commands/RuntimeInputCommand.cs` | Also requires `PIPELINE_HAS_INPUT_SYSTEM_PACKAGE`. Upstream bug: projects with Active Input Handling set to "Both" but no Input System package (common in VRChat) failed to compile. |
| `package.json` | Minimum Unity version lowered to `2022.3`. |

## Install

Clone or copy this repo into your project as `Packages/com.unity.pipeline`, then focus the Editor so it recompiles. Check with:

```
unity status
```

Don't install or upgrade with `unity pipeline install` or the Package Manager, because that replaces this copy with the upstream package.

## Status

Tested on 2022.3.22f1 (Windows) in a clean project and in a VRChat avatar project with the VRChat SDK, VRCFury and Poiyomi. These commands work: `eval`, `create_gameobject`, `set_transform`, `add_component`, `find_gameobjects`, `get_scene_hierarchy`, `list_open_scenes` and `console_status`. Most of the roughly 200 commands are untested. Build, profiling, input simulation, code reload and the test runner may still use Unity 6-only APIs.

## License

The upstream package is © Unity Technologies and is provided under the [Unity Terms of Service](https://unity.com/legal/terms-of-service) (see `LICENSE.md`). This repo is not affiliated with or endorsed by Unity.
