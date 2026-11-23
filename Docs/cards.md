# Notes per card

Grep for the card here before changing it. `X` in "`.X.cs`" = the partial of `SpellServer` (rules), `WorldNet` (ops), `SpellPlans` (sizes).

## Weapon
- **Sword** (Severance, Flowing Cut; was "Blade", the code still says so): `BladeVfx.cs`, `SpellServer.Blade.cs`. The user's `Resources/VFX/Sword.prefab` (grip = "Handle" child, blade along +Y). Fades in and out dithered (`SME/ToonFade`, the user asked for it). Hits flash the Chromatist's lens-shaped slash frames (`SlashFx`).
- **Hammer**: `HammerVfx.cs`, `SpellServer.Weapon.cs`. The user's `Resources/VFX/Hammer.prefab` (hands on "HandleR" / "HandleL", right one nearer the head; head at the origin, handle along +Y, striking face +X). `HammerMotion` drives arms and hammer. `SpellPlans.CraterRadius` drives the slam's bowl, who it hits and the ring.
- **Guns** (Rachmaninoff, Sebastian): `.Guns.cs`, `GunVfx.cs`. The user's Gunbound prefabs `Resources/VFX/Rachmaninoff.prefab`, `Sebastian.prefab` (barrel +X, held at 0.4 scale; TriggerPoint = right hand, OffhandHold = left hand, BarrelPoint = muzzle; recolour by swapping part materials, e.g. `Assets/SME/GunBlock.mat`). `GunMotion` drives arms and gun.
- **Sebastian**: paid when the charge begins; let go early = refund, interrupted = lost; 0.3 s wind-up before it fires; always level (the camera looks freely); body and gun stop turning when it's let go (`FaceLock`); only its caster sees the line. `SebastianRay` / `SebastianReach` give the line, the shot and who it hits.

## Necromancy (sickly green; `SpellServer.Necro.cs`)
- **Undead** are on their master's side (`Combatant.Team`, `Combatant.Allies`: no damage or knockback between allies, one side for winning the round). Summon Horde zombies and raised dummies are `TestDummy`s with a simple server AI.
- **Undying** (passive): a dead player with it pending still counts as standing (`SpellServer.UndyingPending`).
- **Undying Grasp**: `.Guns.cs`, `GraspVfx.cs`. Held-in-place silence is `Combatant.Grasped`. The arms are cut out of the character's own mesh (`GraspArmModel`).
- **Skinwalker**: `PlayerController.Skin` (`SkinState`) is the skin worn: colour, face, name tag and moves. What a player can cast is `PlayerController.Hand`, never `Equipped`. The corpse is used up (`Combatant.ConsumeCorpse`). Off at the next round, or when the loadout changes in practice. Kill feed and scoreboard keep the real name.

## Flame
- **Flame bombs** (Remote Flame Bomb, Remote Detonation, Flame Bomb Toss): `.Bombs.cs`, `BombVfx.cs`. Stuck to the world, or to a body part (`SpellPlans.BombBone` finds it on the body, its ragdoll or its corpse). New lasting flames go in `BombInFlames`, new projectiles go in `HitBombs`, new flame explosions call `FlameSetsOff`, new water counts in `InWater`.

## Water (`WaterVfx.cs`, `SpellServer.Water.cs`)
- Every Water spell puts out fire it touches (fireballs, hellfire walls, burning ground, burning people); fire never breaks water.

## Ground / ice
- **Rock Swarm, Ice Spike**: `.Ground.cs`. Swarm rocks are voxel balls of radius `SwarmRockRadius` (drawn, hit and hit-by at that size), no colliders, placed by `WorldNet.SwarmPosition` on every peer. Ice is its own palette entries (`VoxelWorld.IceValue`, added only while applying ops so palettes match); fire near a spike crumbles it.

## Tarot (`TarotVfx.cs`, `TarotVfx2.cs`, `.Tarot.cs`, `.Tarot2.cs`)
- **The Chariot** rolls the caster into a ball (the user asked for it). `ChariotRadius` drives the ball, the controller, the hitbox (`Combatant.SetBalled`), the tunnel and who it runs over.
- **IX–XVI, Flame Thrower, Icy Breath, Grapple Hook**: `.Arcana.cs`, `.Breath.cs`, `WorldNet.More.cs`, `SpellPlans.More.cs`, `ArcanaVfx.cs`. The Tower's 21k cells are generated alike on every peer (`TowerShape`); only base cell and timing are sent. Lasting states live in `Combatant.Status` (`BodyStatus`); the Wheel's glows are the body outline (`PlayerOutlines`).
- **XVII–XXI** (Star, Moon, Sun, Judgement, World): `.Tarot3.cs`, `TarotVfx3.cs`. Judgement is Arcane Judgment with `kind` = `TarotCircle`; it slides (`JudgmentCenterAt`) and erases per column. The Star's through-walls outline (`SME/LitMask` + `SME/LitOutline`) and The Sun's inverted body (`SME/Invert`) are extra body materials set by `PlayerOutlines`. The Sun's damage follows what each player's own screen shows (`SunGazeWatch`).
- **The World**: sphere in `Voxels/VoxelWorld.Sphere.cs`: one op changes all its cells, meshes catch up later (`StepLate` / `FinishLate`), hidden by `WorldView`'s ball. Its shell is its own indestructible palette entries until someone in it dies. Taken bodies: `BodyStatus.Taken` / `Vanished` (`PlayerMotor.Held`). What the two see: `UI/WorldScreen.cs`, `UI/WorldCinematic.cs` (a stage on layer 31 far below the map).
- **Temperance**: the first passive (`CostType.Passive`, shown as "P"). Costs go through `Cards.CostWith` / `PlayerController.CostOf`; the mana cap is `ManaState.Cap`.

## Collection rules
- **Secret rarity**: never in packs or The Fool's rolls, not counted until owned. The World appears blacked out in the deck once you own every other Tarot card (`Collection.SecretReady`, `UI/SecretReveal.cs`). ALL CARDS in the admin panel leaves it to be found.
- **Companion cards** (`CardInfo.companion`, `Cards.CompanionOf`): never in a pack; given with the cards they go with (Remote Detonation, with anything that places flame bombs), shown behind that card in the shop.

## Engine gotchas
- **Off-screen stages** (layer 31): keep the camera's far plane past the screen outline's fade (`Practice.ViewDistance`) or it paints the empty background grey; `SME/Glow` writes no depth prepass, so outlines of what's behind show through it.
- **Squashing a body** goes through `ProceduralAnimator.Fold` (it resets the model's scale every frame).
- **Strength** grows the model after the pose is solved: props placed by formula go through `SpellPlans.GrowPoint` / `BodyGrow`.
- **Ink hull** (`SME/Outline`) pushes out from the mesh's origin: centre the mesh on it, one piece per limb. Skinned meshes: `SME/HullOutline`.
- **Weapon prefabs** are ProBuilder: instantiate to build the meshes, copy them out (`SwordModel.Part`), destroy.

## The Last Chromatist (the user's previous game; where the VFX style comes from)
- Source: https://github.com/grandrx10/chromatist (missiles, rune circles, hex barrier). Download the zip into the session scratchpad if needed.
- Built game: `C:\Users\Richard\Desktop\ChromatistGame\TheLastChromatist.exe` (zip: `Downloads\ChromatistGameV1.2.zip`; video: `Downloads\TheLastChromatistLetsPlay.mov`).
- Already ported: hex barrier (`Assets/SME/Barrier`, `Resources/VFX/HexBarrier.prefab`), bursts, charge, rune spew and drawing (`ArcaneVfx.cs`), rune circle (`JudgmentView.cs`). Only go back to its source for something not ported.
