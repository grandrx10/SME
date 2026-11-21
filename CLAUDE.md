# Sudden Mage Encounter — notes for Claude

Unity 6000.0.36f1 · URP · Netcode for GameObjects 2.4 · ProBuilder. FFA wizard duel in a voxel (0.25 m) world.
Code: `Assets/Scripts` (namespace `SuddenMageEncounter`). Design docs and new card specs are PDFs the user drops in `Downloads`.

## Visual style: cartoony, everything outlined

The user is strict about this. New effects must match it:

- **Glow + bloom, not particles.** Spell energy is flat unlit HDR colour (`SME/Glow` shader, colours well above 1, so URP Bloom makes them glow). Helpers are in `Spells/ArcaneVfx.cs`: `Glow.Piece`, `Glow.Trail`, `EnergyBurst`, `ArcaneCharge`, `RuneDrawing`.
- **Every shape gets a dark ink toon outline.** Solid meshes get the inverted hull (`SME/Outline`, which `Glow.Piece` adds for you). Strips, trails and lines get a wider ink copy drawn underneath (`Glow.Underlay`, `ToonLine`). Colour: `Glow.Ink`.
- **Shrink, don't fade.** Effects scale down to nothing; they never go transparent. Use punchy bursts and camera shake (`CameraRig.ShakeFrom`).
- **Wind** is cartoony inked swooshes and trails (`Spells/ToonWind.cs`). Never dusty smoke.
- **Fire** is outlined flame tongues with yellow, orange and red HDR (`Spells/FireVfx.cs`).
- **Arcane** is deep saturated blue with rune drawings and rune circles.
- **Element colours** (cards and effects): Flame red, Arcane blue, Lightning yellow, Wind grey, Ground light brown, Tarot mahogany (Emperor effects gold), Water cyan/aqua (healing symbols green).
- **Water** (`Spells/WaterVfx.cs`, server rules in `Spells/SpellServer.Water.cs`): every Water spell puts out fire it touches (fireballs, hellfire walls, burning ground, burning people); fire never breaks water.
- **Card art** is the user's own sketches from the PDFs (or pasted images), with lines thickened. Don't redraw them. `Tools/CardArt/CardArt.cs <outDir> --pdf <file.pdf> <CardId...>` or `--png <file.png> <CardId> ...` extracts them to `Assets/Resources/CardArt/<CardId>.png`. Give a new PNG a `.meta` copied from an existing card's (new guid) so it imports the same way.
- **UI:** minimal text. Show things with icons and colour instead of explaining them in words.

## Reference: the user's previous game, "The Last Chromatist"

The VFX style above comes from this game. Read its code before making new effects.

- Source: https://github.com/grandrx10/chromatist (VFX for the arcane missiles, rune circles, hex barrier). It was last unpacked into a temporary session folder that may be gone, so download it again (e.g. into the session scratchpad, outside this project). This PC has no git, so get the GitHub zip.
- Built game, zipped: `C:\Users\Richard\Downloads\ChromatistGameV1.2.zip`
- Built game, unpacked and playable: `C:\Users\Richard\Desktop\ChromatistGame\TheLastChromatist.exe`
- Gameplay video: `C:\Users\Richard\Downloads\TheLastChromatistLetsPlay.mov`
- Already ported: hex barrier (`Assets/SME/Barrier`, `Resources/VFX/HexBarrier.prefab`), energy bursts, charge, rune spew and rune drawing (`ArcaneVfx.cs`), rune circle (`JudgmentView.cs`).

## Working here

- The Unity editor is usually open, so check compilation offline with `powershell -File Tools/check.ps1` (it uses the bundled Roslyn of whichever editor `ProjectSettings/ProjectVersion.txt` names, and finds the project from its own location). Run it after every change. It cannot run gameplay, so tell the user what still needs testing in Unity.
- There is no Python or .NET SDK on this PC. Git is at `C:\Program Files\Git\cmd\git.exe` (may not be on PATH); repo is https://github.com/grandrx10/SME. To push from the agent shell, set `$env:GCM_INTERACTIVE="always"; $env:GIT_TERMINAL_PROMPT="1"` first. `Assets/StreamingAssets/Tools/*.exe` are stored in Git LFS (ffmpeg is over GitHub's 100 MB limit).
- Networking is server-authoritative. Voxel edits go through the ordered op stream in `Spells/WorldNet.cs` as integer cell ops, so every peer's world stays identical. Spell logic runs on the server in `Spells/SpellServer.cs`. Input, aiming and previews are in `Spells/AbilityCaster.cs`. Debris and effects are client-side only.
- Spells have a 20 m max range unless the card says otherwise. Knockback ragdolls people. A caster's own spells don't hurt or ragdoll them unless the card says so.
