# Design and architecture

## Architecture

```
Autoloads (src/autoload)
  Db        loads every file in data/ once; game code never parses files itself
  Settings  display, audio, mouse, key bindings (user://settings.cfg)
  Progress  save file: codex unlocks, best stars, known hosts (user://save.cfg)
  Net       host/join/solo, lobby roster, session start
  Sfx       plays sounds by id from data/audio.cfg, skips missing files

Surgery scene (scenes/surgery.tscn, src/surgery/surgery.gd)
  Room        builds geometry, lights and stations per environment (or, ambulance, sidewalk)
  Patient     host-authoritative simulation (vitals, wounds, drugs, targets, grips)
    PatientBody   mannequin, surgical site tissue layers, cavity, organs, wound map (every peer)
      TissueSim   soft tissue sim of the site skin (every peer, tears decided by the host)
  Tools       ToolManager: every grabbable item, grab/release/belt requests
  Surgeons    one Surgeon per player: input, hands with two-bone IK, personal gauges
  Systems     Objectives, EventDirector, Scoring, Nurse, Lab (host only)
  Hud         screen UI and full screen overlays
```

### Networking model

- **Host is the authority** for the patient, tools, objectives, events and score.
- **Each peer owns its surgeon**. It streams body, head and hand state at 30 Hz (unreliable).
- **Tools are simulated on the host.** It reads each hand's synced position and "using" state and runs `ToolActions`.
  This keeps all game logic in one place and makes cheating or desync between two co-op players a non-issue.
- **Skin damage is painted by broadcast.** The host decides what to paint and sends paint ops (reliable),
  every peer paints its own copy of the wound map, so textures stay identical without sending images.
- **Tissue topology is broadcast the same way.** Cuts, stitches, bursts and snapped springs are reliable RPCs,
  every peer runs its own copy of the tissue sim. Only the host lets springs snap, then tells the others which one.
- Vitals, targets, organs and tissue grips sync at 5 Hz. Free-falling tools sync at 10 Hz.
- Solo play is the same code with an offline peer. There is no separate single player path.
- Every peer builds the same patient and tray from the session seed, so setup needs no RPCs.

### Look

- Cel shading (`toon.gdshader`): banded light angle, hard specular, rim light, procedural grime.
  Room surfaces use a smooth variant so walls don't band.
- Ink outline via inverted hull (`outline.gdshader`).
- Surgical site tissue (`tissue_sim.gd`, `patient_body.gd`): the skin is a separate soft layer over fat and muscle.
  - The skin is a grid of particles joined by springs under tension, loosely anchored to the body.
    Cutting severs springs, so an incision gapes on its own; forceps and retractors pin particles and stretch it further.
  - Each severed spring remembers how deep the cut went: skin, fat or muscle.
    Skin, fat and muscle are three meshes rebuilt from the sim; each one drops the triangles over a gap cut down to it.
    So a shallow cut shows yellow fat, a deeper one red muscle, and only a full depth cut opens into the cavity.
  - Overstretched springs snap into a tear (host only). Stitches are extra springs across the cut, their length is the tension.
  - The sim sleeps when nothing moves.
- Skin damage (`skin.gdshader` + `WoundMap`): two painted textures drive cut grooves, burns (red halo to charred core),
  bruises (purple to yellow), stitches, blood pooling, marker ink, iodine and grime. Fat and muscle use `tissue_layer.gdshader`.
- Cavity blood rises as a glossy pool when bleeding inside, drops with suction.
- Screen grading (`post_grime.gdshader`): desaturated sick-green tint, vignette, film grain, chromatic split.
  Sickness wobbles and blurs the view, passing out blacks it out.
- The patient monitor beep is generated in code and its pitch follows SpO2, like a real pulse oximeter.

## Data formats

### Quirks (`data/quirks/*.md`)

Markdown, one `## id` heading per quirk, `- key: value` lines.
Any key can be overridden per variant with `key.variant`. Icons are relative links, clickable from an editor.
`effects` is what code reads: `key=value, key=value`. `_mult` keys multiply, other numbers add, text values are lists split by `|`.

Patient effect keys:

| Key | Meaning |
|---|---|
| glucose_drift | mmol/L per second of rising blood sugar |
| seizure_chance | seizures per second |
| heal_rate | wound closure per second (every wound) |
| bleed_mult | bleeding multiplier |
| clot_risk | cardiac arrest chance per second unless heparin is active |
| bone_hardness | 2 = regular saw useless |
| bone_fragile | heavy dropped tools break bones |
| saw_speed_mult | saw progress multiplier |
| allergen | drug id(s) that cause an allergic reaction |
| arrest_mult | cardiac arrest chance multiplier |
| blood_ml_mult | starting blood volume multiplier |
| rare_blood | only the Bombay blood pack is compatible |
| anesthesia_decay_mult | anesthesia wears off faster |
| talk_rate | how often an awake patient talks, >1 adds hints and lies |
| panic_mult | panic build-up multiplier |
| fat_depth | reduces cut depth, weakens retraction |
| mirrored | targets mirrored left-right |
| spo2_offset | baseline oxygen offset |
| cough_chance | coughs per second (jolts hands) |
| sedation_mult | anesthesia and sedative strength |
| whiskey_friendly | whiskey calms without side effects |
| pacemaker | cautery and shocks near the chest cause misfires |
| mh_trigger | anesthetic gas causes malignant hyperthermia |
| tear_threshold_mult | how far skin stretches before it tears |
| bruise_mult | bruise size |
| ticklish | awake patient flinches when touched outside the site |
| pain_mult | pain multiplier |

Surgeon effect keys:

| Key | Meaning |
|---|---|
| tremor / tremor_mult | hand tremor amplitude (m) and multiplier |
| bump_resist | 0..1 less likely to drop tools when jolted |
| belt_slots | belt capacity change (default 4) |
| items | personal tool ids spawned on the belt |
| manual_highlight | manual pages matching the patient glow |
| fine_tools_blocked / heavy_tools_blocked | tool size restrictions |
| grip_strength_mult | clamp and retractor pull strength |
| bad_breath | sickness per second given to a partner closer than 0.9 m |
| cough_chance | coughs per second (jolts own hand) |
| stress_mult | stress build-up multiplier |
| sweat_rate | glove sweat per second |
| blood_sickness_rate | sickness per second while the patient bleeds |
| improvised_mult | lower = improvised tools work closer to proper ones |
| nurse_delay_mult | nurse delivery time multiplier |
| move_speed_mult | walking speed |
| switch_delay_mult | hand switch delay (0 = instant, idle hand steady) |
| sickness_immune | no sickness at all |
| contamination_vision / dirty_stress | see dirty tools, stress when touching them |
| caffeine / drink_steady | drink buffs |
| qte_window_mult / defib_charge_mult | turning and defibrillator timing |
| deaf | no sound, subtitles only |

Rolling rules live in `src/data/quirk_roller.gd`: 1-3 surgeon quirks, with 3 at least one positive and one negative
(`mixed` counts as both), `exclusive` quirks alone.

### Scenarios (`data/scenarios/NN_id.cfg`)

`[scenario]` holds metadata and setup, `[patient]` the starting wounds, burns, internal wounds and cavity targets,
`[objectives]` the steps. The file name gives the id (`03_appendectomy.cfg` -> `appendectomy`), the number the order.

Objective step types (`src/surgery/objectives/objective_checks.gd`):
`sanitize, iv, anesthesia, local_block, mark, incise, extract, close, close_internal, stop_bleeding, stabilize, calm,
inject, defib, tourniquet, clamp, transfuse, flip, align, debride, graft, listen, comfort, wait`.
Required steps complete in order, `"optional": true` steps any time for bonus points.

Target `remove_with`: `clamp` (grab and pull out, `anchor` > 0 needs cutting or a slow pull first),
`suction` (drain `amount`, `refill` per second), `saw` / `smash` (bone).

### Other data

- `tools.cfg`: every item, its action and numbers. Backup tools reuse actions with worse numbers.
- `drugs.cfg`: effects, onset, duration, flags, dangerous combinations, blood types.
- `events.cfg`: random escalation events, weights and cooldowns.
- `scoring.cfg`: points and stress per action, star thresholds.
- `consequences.cfg`: post-op report rules (infection, burst staples, lawsuits).
- `dialogue/patient_lines.cfg`: patient speech by trigger and age.
- `audio.cfg`: sound id to file.

## Mechanics

### In this draft

- Two-hand control, one active at a time, idle hand frozen mid-action. Pressure levels, tilt and twist.
- Holding tissue anchors the hand; walking away tears it.
- Hand bumps between surgeons, lift to pass over. Jolts from seizures, coughs, potholes, pedestrians.
- Cuts with depth and speed (clean vs jagged) through skin, fat and muscle. Soft tissue sim: cuts gape, retraction widens, overpull tears.
- Per-segment closure: sew along the whole wound. Weak closures burst under strain.
- Bleeding per wound, blood pooling on skin and in the cavity, suction, gauze pressure, clamps, cautery, tourniquet.
- Drugs with onset/duration curves, direct vs IV routes, allergies, dangerous combinations, blood type matching.
- Cardiac arrest: V-fib, asystole, shocks, adrenaline windows, zapping a partner who's touching the patient.
- Seizures, malignant hyperthermia, diabetes drift, anesthesia wearing off, panicking awake patients.
- Organs you push aside, targets you free by cutting, sawing, slow pulling or suction.
- Dropped tools: floor makes them dirty, dropping into the cavity cuts something, heavy tools break fragile bones.
- Sterility tracking into the post-op report (infection, amputation).
- Nurse orders with a global cooldown, blood panels with narrow/fast vs full/slow choices.
- Turning the patient as a shared quick time event, all surgeons on one side.
- Personal gauges: stress (pass out), sickness (vomit), breath (steady hands), sweat (slippery gloves, drips).
- Belt inventory, personal quirk items, drinking and wearing items.
- Manual with Divine knowledge highlights, patient card with red herrings.
- Scoring, stars, delayed consequences, codex unlocks, 23 scenarios.

### Approved mechanics (in this build)

- **Pass the tool**: press Grab with a tool near your partner's empty hand to hand it over. Moving hands fumble it onto the floor.
- **Organ handling damage**: organs held out of place for more than a few seconds, or shoved hard, bruise (they darken) and ooze.
- **Suture tension**: the pressure level sets stitch tension for the needle and paper clips. Loose leaks, tight can tear through.
- **Run modifiers** (`data/run_modifiers.cfg`): 1-2 per run, rolled in the lobby so both players see them before starting.
  Understaffed, expired drugs, bad wiring, med student, short supplies, blood shortage, broken heating, chart mix-up.
- **Chart mix-up** (a run modifier): the patient card shows a wrong blood type and allergy and misses a real condition,
  until the nurse brings the corrected copy partway through.
- **X-ray cart**: push it to the table, take an exposure, and an instant print develops over six seconds.
  It shows metal (bullets, knives, retained tools), bone, masses and trapped air.

### Ideas not yet approved

- Scrub nurse mode, anesthesia machine station, vital-sign callouts, patient's family calling mid-surgery.

## Assets and animation

All models and sounds are generated from code (`./build.sh assets`), so they can be regenerated and tweaked.

- **Organic and rigged models** (`tools/blender`, Blender as a Python module): the patient, the surgeon's glove,
  organs and anatomical targets. Metaball and tube shells are fused with a voxel remesh, shaped with scripted
  sculpt strokes, decimated and skinned to a bone rig (bone heat weights, jaw weights set by rule).
  The patient is one skinned mesh; eyes, lids, hair, brows, teeth and tongue ride their bones.
- **Hard-surface models** (`tools/assetgen`, trimesh): props and instruments from lofted tubes, lathes, rounded boxes
  and extrusions, exported to `.glb` with named parts.
- The game swaps materials for the cel shader by name (`src/visual/model_slot.gd`).
- **Patient skin**: `site_heights.json` is baked by raycasting the body at rest, so the surgical site hugs the body.
  The body (and gown) is cut away under the site and the simulated skin layer takes its place.
- **Animation** is procedural and driven by synced game state, so it matches on every peer:
  - Patient (`patient_animator.gd`, bones posed through `bone_rig.gd` in model-space axes): breathing at the
    respiration rate (the trunk and surgical site rise together), eyes open when conscious,
    jaw moves while talking, head tracks and flinches with pain, panic flails, seizures shake every joint.
  - Surgeon: walk cycle from movement speed, collapse when passed out, head tilt from camera pitch,
    two-bone IK arms, glove finger bones relax, wrap around a held tool and squeeze while using it.
  - Tools (`tool_animator.gd`): jaws open and close, plungers push, stapler triggers squeeze, saw blades oscillate,
    lighter flame and cautery tip light up, defibrillator charge light blinks.
- **Sounds**: 37 effects synthesized from noise, oscillators, filters and formants (tissue, tools, room tone loops,
  surgeon, patient groans/screams/breathing). The monitor beep is generated live.
  Patient speech is subtitles; optional recorded lines can be dropped into `assets/audio/voice/`.

## Known limits of this iteration

- Hands have finger curl but no per-tool grip poses.
- Physics pushing of organs runs on the host only; clients see synced positions.
- Tilt and twist change tool orientation and tip position, but no target yet requires a specific twist.
- Late joining mid-surgery isn't supported. Both players join in the lobby.
