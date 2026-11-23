# Adding a card

Find each place with Grep on the anchor, read ~40 lines round it, edit. Don't read whole files: the big ones are 1000–2500 lines.
Copy the structure of the closest existing card. Files under `Assets/Scripts/`.

## Pick a card to copy

| The new card… | Copy |
|---|---|
| press, timed release aimed by the owner | Rachmaninoff (`_shotAt`, `TickGuns`, `FireShotgun`) |
| hold to charge, pays at the start, refund on early release | Sebastian (`ReleaseRifle`, `RiflesS`) |
| hold to aim, line along the ground | Ice Spike, Undying Grasp (`GraspGround`, `ShowGrasp`) |
| flying projectiles | Rachmaninoff pellets (`FlyPellet`), The Star (`FlyStar`) |
| beam / vaporised ground | Sebastian (`SebastianBeam`, `VoidRise`), Arcane Judgment |
| dash into a melee hit | Rising Hammer, Severance |
| held channel | Flame Thrower (`SpellServer.Breath.cs`) |
| stuck objects triggered later | Flame bombs (`SpellServer.Bombs.cs`) |
| lasting state on a body | `BodyStatus` (chill, grasp, Strength) |
| passive | Temperance, Undying |
| held weapon model | `GunVfx.cs` (follows pitch), `HammerVfx.cs` (fixed gesture) |

## Checklist

New code goes in partial files of its own (`SpellPlans.X.cs`, `SpellServer.X.cs`, `WorldNet.X.cs`, `XVfx.cs`), hooked in below.

1. **`Cards/Cards.cs`**: `CardId` (append, never reorder; saved and networked), an `All` entry, `CastTime`. `castOnPress = true` = casts on key down; else hold to aim, cast on release.
2. **Art**: `powershell -File Tools/cardart.ps1 -Pdf <pdf> -Cards Id1,Id2` (in the order the sketches appear).
3. **`SpellPlans.X.cs`**: every number. Looks and server both read these.
4. **`Player/NetState.cs`**: `SpellPose` (append). A lasting state: a `BodyStatus` field + `NetworkSerialize` + `Equals` + a helper; clear it in `PlayerController.OnServerKilled`.
5. **`Player/ProceduralAnimator.cs`**: a `Key[]` (wrist offsets from the chest, finger direction, twist, drop, lean) + a `PoseData` entry. Kneeling: the `bool kneel =` line.
6. **`Spells/AbilityCaster.cs`**: `TryCast` case (local checks, `SetPose`, `Motor.Root/FaceLock/...`); the `gestures end on their own` switch; if held: the pose in `Press` and a case in `ShowIndicator` (shapes in `SpellIndicator.cs`). Owner-timed second step: a `_xAt` timer ticked in `Update`, reset in `CancelAll`, added to the `knocked && (Casting || …)` line.
7. **`Player/PlayerController.cs`**: owner → server RPCs (`RequestX` + `[Rpc(SendTo.Server)]`).
8. **`Spells/SpellServer.cs`**: the `Cast` switch, `Reset`, `Tick`; `OnInterrupted` (hit/ragdolled) or `Fizzle` (knocked out of the cast). Practice reset: `ServerClearPractice` in `SpellServer.Arcana.cs`. Blocks other cards while running: `BusyChanneling`.
9. **`Spells/WorldNet.X.cs`**: an op enum continuing the numbers (grep `enum .*Op : byte`; used up to 123), `ServerX(...)` writers, `ApplyX`. Hook `ApplyX`, `AnimateX`, `ClearX` (and `ClearXIn(minX, maxX)`) into `WorldNet.cs` beside the existing ones.
10. **Looks**: skip them when `Debris.Suppressed` (a late joiner replaying the log). A per-body view: `AddComponent` in `Combatant.Awake` (everyone) or `PlayerController.Awake` (players).
11. `Tools/check.ps1`; then add only what isn't obvious from the code to `Docs/cards.md`.

## Server rules to respect

- Hurt/throw through `Hit(victim, attacker, damage, point, knock, ragdoll)`. Skip `Folded(c)`, `Shielded(c)` (barrier, bubble, untouchable), `Combatant.Allies(a, b)`; movement also skips `Unmovable(c)`, `Hanging(c)`. Taking someone's control without damage: `c.ServerCredit(caster)`.
- Projectiles, every frame: `BarrierBlocks`, `BubbleBlocking` + `HurtBubble`, `SweepBall`, then end. Add a `ReflectX` beside `ReflectStars` (The Hierophant) and add it to `HitBombs`.
- Flame explosions call `FlameSetsOff`; water must count in `InWater`; fire near ice: `MeltIceNear`.
- What a player can cast is `PlayerController.Hand`, never `Equipped`. A card rolled by The Fool arrives with `free`.
- Owner-timed actions: validate NaN, distance and time (about 0.15 s early is fine), and give the server a fallback (about 0.3 s late, along `ViewDir`).
- `ServerKnock` with a ragdoll counts for combo breaks (so does `ServerRecoil`, a caster thrown by their own spell).
- Voxel edits only inside `ApplyX`, from integer cells the server sent (`RemoveCells(cells, removed, true, islands)`). Never recompute cells from floats on each peer.
- Pose, yaw and pitch replicate (`ViewPose`, `ViewPoseTime`, `ViewYaw`, `ViewPitch`), so most gestures need no op. Owner-only visuals: check `IsOwner`.

## Looks helpers

`Glow.Piece` (shape + ink), `Glow.Trail`/`DropTrail`, `EnergyBurst.Create`, `ArcaneCharge`, `RuneSpew`, `RuneDrawing` (`ArcaneVfx.cs`); `ArcanaVfx.Line`/`SetLine` (inked line); `JudgmentView.Tube` (beam); `MuzzleFlash`; `VoidRise.Spawn` (vaporised cells, rim colour); `Debris.Spawn`; `CameraRig.ShakeFrom`; `SwordModel.Part` (any prefab on the dither-fade shader); `SpellVisuals.RightHand/LeftHand`; `GraspArmModel` (an arm cut from the character).
