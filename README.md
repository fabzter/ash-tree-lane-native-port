# The House On Ash Tree Lane — no-editor native port research

A reverse-engineering research workspace that takes a shipped **Windows Unity game** and makes it
run **natively on macOS (Apple Silicon, Metal)** — and builds it for Windows / macOS / Linux —
**without ever installing the Unity editor**.

> ## 🙏 Credit where it is due
> **The game itself is not ours.** *The House On Ash Tree Lane* is a free puzzle/exploration game
> by **GingerEnslaver**, published on itch.io: <https://gingerenslaver.itch.io/ashtreelane>.
> All game code, assets, art, and design are © GingerEnslaver. This repository contains **only our
> research tooling, build scripts, and notes** — it does **not** redistribute the game. You must
> obtain your own copy from the author's page to use these tools. Please support the original author.
>
> The native-Metal shader-port technique was pioneered by **Andreansx** in
> [`casualties-unknown-apple-silicon`](https://github.com/Andreansx/casualties-unknown-apple-silicon)
> (a native Apple-Silicon port of another Unity Windows game). We studied and adapted that work;
> thank you for publishing it.

## What this is

The shipped game is a **Unity 2021.1.11f1, Mono-backend** Windows build. A Unity player is
`native engine + serialized assets + managed assemblies`. Only the managed game logic
(`Assembly-CSharp.dll`, 45 types) is platform-agnostic; the engine and the compiled shaders are not.

This project shows, step by step, how to:

1. **Decompile & recompile the game logic** from our own sources against the shipped Unity managed
   libraries, producing a byte-size-identical, functionally-equivalent `Assembly-CSharp.dll`
   (`decomp/`, parity-verified across all 45 types).
2. **Assemble cross-platform players** (Windows / macOS / Linux) from Unity's official standalone
   player runtimes — no editor (`build/assemble.py`, `build/fetch_players.sh`).
3. **Fix the macOS windowed launch** (the player needs `MainMenu.nib` +
   `NSPrincipalClass=PlayerApplication`, otherwise the Cocoa delegate never fires).
4. **Port the D3D11-only shaders to native Metal** so the Mac build actually renders instead of
   falling back to a Null graphics device and crashing (`build/metal_port/`). This is the hard part,
   and it works: the title screen and scene render on an Apple M2 Pro.
5. **Remove Unity entirely** (`build/unityfree/`): run the verbatim decompiled C# on a clean-room
   `UnityEngine` shim over **SDL3 `SDL_GPU` (native Metal)** — ~116–136 MB vs 855 MB, and match the
   original's looks (full-res lightmaps, flat normals, default post-fx). See `notes/unity-free-gotchas.md`.

## Results

| Target | State |
|---|---|
| Windows | renders (D3D11), verified via CrossOver on Apple Silicon |
| macOS | **native arm64 Metal render** — no Wine/Rosetta at runtime, no editor |
| Linux | player assembled; engine + logic verified (real x86_64 hardware needed for full run) |
| macOS (Unity-free) | **SDL3/Metal, no Unity** — ~116–136 MB; plays + post-fx; matches original look |

## Layout

- `decomp/` — decompiled sources + the lib-only recomp build (`build.sh`) and parity gates.
- `build/` — cross-platform assembly (`assemble.py`, `build.sh`, `fetch_players.sh`), the Metal shader
  port (`metal_port/`), the **Unity-free SDL3 rebuild** (`unityfree/`), and plans (`PORT_PLAN.md`,
  `UNITY_FREE_PLAN.md`).
- `notes/` — research logs: recomp, cross-platform build, shader-port TODO→done, lessons learned.
- `AGENTS.md` — guidance for AI agents working in this workspace.

## What is NOT in this repository

To respect the author's rights, the public repo excludes the copyrighted game payload and other
large/licensed artifacts (see `.gitignore`): the original download, the extracted game assets,
the verbatim decompiled game source, rebuilt DLLs, built `.app`/players, and the Unity player
modules. The tooling here operates on a copy of the game you obtain yourself.

## Reusable knowledge

The durable, generalizable lessons live as agent skills in
[`fabzter/agent-skills`](https://github.com/fabzter/agent-skills):
- `unity-mono-lib-only-recomp` — decompile/recompile + cross-platform player assembly + Mac launch fix.
- `unity-no-editor-metal-shader-port` — the D3D11→Metal no-editor shader port.
- `unity-free-sdl3-rebuild` — remove Unity; verbatim C# on SDL3/Metal shim; asset re-encoding; fidelity.
- `port-fidelity-gap-audit` — "it runs ≠ it matches": systematic visual/audio/behavioral gap checklist.

## Run the Unity-free build

```bash
open "build/unityfree/dist/macos/The House On Ash Tree Lane.app"
```
WASD/mouse, **E** interact; **F1–F6** post-fx (none/bloom/kuwahara/pixelart/dog/vignette). CLI stills:
`--scene 0/1/2 --postfx X --frames N --screenshot out.png`. Compare vs original:
`build/unityfree/verify/side_by_side_v3.png`.

## License & intent

This is a personal, non-commercial research/interop project. Our tooling and notes are shared for
learning. The game and its assets remain the property of GingerEnslaver; nothing here is a
replacement for, or a redistribution of, the original work.