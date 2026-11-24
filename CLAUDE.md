# Sudden Mage Encounter

Unity 6000.0.36f1 · URP · Netcode for GameObjects 2.4 · ProBuilder. FFA wizard duel in a voxel (0.25 m) world.
Code: `Assets/Scripts` (namespace `SuddenMageEncounter`). Card specs are PDFs in `Downloads`.

**Read only when the task needs it:**
- `Docs/adding-a-card.md`: before adding a card or a new spell mechanic (every place to edit, helpers, server rules).
- `Docs/cards.md`: Grep it for a card before changing that card; also engine gotchas and The Last Chromatist.

## Working economically

- Grep for the anchor and read the lines round it; don't dump whole files (the big ones are 1000–2500 lines, and tool output over 30 KB is cut off).
- Write new code with Write, one file per call: very long shell commands fail outright.
- Many small edits: a script of exact-match replacements that reports anything not matched exactly once, then one compile check.
- Small tweak: edit, check, two-line answer. Keep final summaries short.
- Other Claude sessions may be working in this folder: re-read a shared file just before editing it, and change only what you need.

## Map

| | |
|---|---|
| `Cards/Cards.cs` | every card: id, cost, text, cast time |
| `Spells/AbilityCaster.cs` | owner: keys, aiming, local checks, gestures |
| `Spells/SpellServer*.cs` | server: what spells do (partials by group) |
| `Spells/WorldNet*.cs` | the ordered op stream every peer applies; voxel edits as integer cells |
| `Spells/SpellPlans*.cs` | sizes, timings and shapes, shared by looks and rules |
| `Spells/*Vfx.cs`, `*View.cs` | looks (client-side only) |
| `Cosmetics/` | crates' cosmetics: list, rolls, wardrobe, how they're worn (prefabs in `Resources/Cosmetics`; `CosmeticLook` names mark their renderers) |
| `Player/` | `PlayerController` (RPCs, mana), `PlayerMotor` (movement), `Combatant` (health, ragdoll), `ProceduralAnimator` (gestures), `NetState` (poses, `BodyStatus`), `TestDummy` |
| `Hazards/` | map hazards (components inside arena prefabs) |
| `Voxels/`, `Core/`, `UI/`, `Editor/` | world, match flow and practice, menus and HUD, arena baking |

Server-authoritative: the server decides, peers apply the same ops in order; debris and effects are local.

## Rules

- **What's drawn is what it does.** One constant drives the visual size and the gameplay radius; nothing visible pokes past it. An outlined sphere shows 1.28× its size: scale by `ForceView.OutlineGrow`. `Glow.SphereMesh` has radius 1: use `Glow.SphereScale(r)`.
- Voxel op radii are `r4` = quarter-cells: `SpellPlans.R4(metres)`, never `metres * 4`.
- 20 m max range unless the card says otherwise. Knockback ragdolls. A caster's own spells don't hurt or ragdoll them unless the card says so.
- **Use the user's models** for bodies, hands and weapons (the character mesh, their prefabs); never make up stand-ins.
- **Card art** is the user's own sketches; never redraw them.
- **UI:** minimal text; icons and colour instead of words.

## Visual style: cartoony, everything outlined (the user is strict)

- **Glow + bloom, not particles**: flat unlit HDR colour (`SME/Glow`, well above 1). Helpers in `Spells/ArcaneVfx.cs`.
- **Dark ink outline on every shape**: solids get the inverted hull (`Glow.Piece` adds it), strips and lines a wider ink copy under them (`Glow.Underlay`, `ToonLine`). Colour `Glow.Ink`.
- **Shrink, don't fade.** Punchy bursts, camera shake (`CameraRig.ShakeFrom`). Only held weapons fade (dithered, `SME/ToonFade`).
- Wind: inked swooshes (`ToonWind.cs`), never dusty smoke. Fire: outlined flame tongues (`FireVfx.cs`). Arcane: deep blue, runes.
- **Colours**: Flame red, Arcane blue, Lightning yellow, Wind grey, Ground light brown, Tarot mahogany (Emperor gold), Water aqua (healing green), Weapon steel white (slashes hot white), Necromancy sickly green, Hex purple (Shatter white, Stasis turquoise, Mind purple).

## Tools

- `powershell -File Tools/check.ps1`: offline compile (the editor is usually open). Run after changes. It can't run gameplay: say briefly what to test in Unity.
- `powershell -File Tools/cardart.ps1 -Pdf <pdf> -Cards Id1,Id2` (or `-Png <file>`): sketches → `Assets/Resources/CardArt`, metas included.
- No Python or .NET SDK here; Unity's bundled `dotnet` + `csc` work (see `check.ps1`).
- Git: `C:\Program Files\Git\cmd\git.exe`, repo https://github.com/grandrx10/SME. To push: `$env:GCM_INTERACTIVE="always"; $env:GIT_TERMINAL_PROMPT="1"` first. `Assets/StreamingAssets/Tools/*.exe` are in Git LFS.
