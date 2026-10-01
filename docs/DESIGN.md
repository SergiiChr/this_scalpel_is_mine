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
- **Spotty connections don't end the session.** ENet drops a peer only after 15-45 s without answers
  (`Net.TIMEOUT_*`, ENet's own default is about 5 s). Reliable RPCs queue up and arrive once the link recovers,
  unreliable state just picks up with the next packet.
  A 4 Hz heartbeat tracks how long each peer has been silent: after 0.75 s the host pauses that surgeon's tools
  (not released, so a charged defibrillator doesn't fire and clamps keep their grip), after 1.5 s the HUD says
  who the connection is waiting on. Joining gives up after 10 s if the host never answers.
- A partner who drops out for good leaves the room and their tools fall where they were. The host refuses joins
  while a surgery runs. A client that loses the host goes back to the menu with the reason shown.

### Look

- Palette (`Materials`): surgical green for walls, gowns and drapes, cool fluorescent white for every room light,
  a warmer white only for the surgical lamp. The two surgeons wear green and ceil blue scrubs.
  Colors outside that family are for things that should stand out: the red crash cart, blood, drug labels.
- Lighting: a ceiling panel light over the table lights the room from above, the surgical lamp's spot focuses
  on the site, dim tubes fill the corners. Outside, the streetlight does the ceiling light's job.
  The flicker event dims every room light but the surgical lamp.
- Shading (`toon.gdshader`): smooth diffuse, soft specular, rim light, procedural grime.
  Every model material belongs to a family (`Materials.FAMILIES`, by material name): skin (light wraps past the
  terminator with a red tint), glove rubber, cloth (no highlight, soft sheen), metal (tinted highlight, a fake
  ceiling/floor reflection), plastic and wet tissue. The model's color, roughness, metallic and texture maps are kept.
  Grime rides on the model; only walls and floors keep theirs fixed in the world. Room surfaces skip specular and rim.
- Fine relief per family (`Materials.Detail`): skin pores, glove creases, cloth weave and folds, brushed steel.
  It only tilts the normal, so silhouettes and collisions are the model's own.
- Surgical drape (`drape.gd`, operating room only): a sheet over the torso and legs laid from the body's rest mesh,
  1.2 cm off the skin so breathing never pushes the body through it, rising with the trunk. Its opening frames the
  site and covers the site's edge. It hides while the patient is turned away from the site; hands rest on it.
- Organs and cavity walls (`flesh.gdshader`) show branching vessels and mottling.
- Ink outline via inverted hull (`outline.gdshader`): about 1.4 px wide at any distance, capped by the part's size,
  so a blade gets a hairline and furniture a full line.
- Surgical site tissue (`tissue_sim.gd`, `patient_body.gd`): the skin is a separate soft layer over fat and muscle.
  - The skin is a grid of particles joined by springs under tension, loosely anchored to the body. Cells are about
    6 mm square on every site (`TissueSim.CELL`). Only an active window is simulated: around cuts, grips and skin
    that moved, plus a margin of still skin. It only grows while the skin moves and is picked afresh once it sleeps.
    Cutting severs springs, so an incision gapes on its own; forceps and retractors pin particles and stretch it further.
  - Each severed spring remembers how deep the cut went (skin, fat or muscle), where the blade crossed it and which
    way the cut ran. The edges of a cut are drawn back square to it, more the deeper it goes: skin gapes a few
    millimeters, fat more, cut muscle retracts hard. The pull tapers off toward the cut's ends (where each stroke
    starts and where the blade is), so a cut opens like a lens, closed at both ends, like a zipper behind the blade.
  - Skin, fat and muscle are three meshes rebuilt from the sim, only where the simulated skin replaces the body (the
    region). A layer cut through is split exactly where the blade crossed each spring, not along the grid: each side
    keeps its part of the triangle and moves with it. Walls run down each lip through the layer's thickness (pale
    dermis, yellow fat, red muscle), so a cut has depth: a skin cut shows the fat (or the muscle where there's no fat),
    a deeper one the muscle, a full depth cut the bone or organs under it. The meshes rebuild on the frame after the
    sim steps, so the two costs don't land on one frame.
  - Fat is per site (`fat` in `patient_sites.json`): none on the forearm, where a cut deeper than the skin goes into the
    muscle, thickest on the belly.
  - Overstretched springs snap into a tear (host only), and clients snap the same spring by its index. Springs to the
    site's fixed border or to skin hanging off the body never snap; springs stretched at rest (the edge of a round limb)
    break only well past that.
    Stitches are extra springs across the cut, their length is the tension. Thread is stiffer than skin (solved more
    often). A stitch closes a few millimeters of the cut. A cut counts as closed only where its edges meet: a loose
    stitch leaves a gap that stays open and bleeds.
  - Tools and hands touch the skin as it's deformed now (`TissueSim.skin_height()`), not the body's rest shape, so a
    lifted fold is where it's drawn. Over the site a hand rests on that skin, not on the gown's or the site's colliders.
  - A grip holds the skin within 8 mm of its jaws and drags a patch around it along (never across a cut), so pulls
    spread and the skin stretches visibly over several centimeters before it tears. Everything that moved is shown
    simulated.
  - Cut muscle retracts and pulls the edges further apart. It's sewn from inside the wound (`TissueSim.muscle_stitch()`,
    `Patient.close_muscle_at()`), and skin won't close over open muscle: it refuses, or a tight stitch tears through.
  - The skin settles under its own tension when it's built, so it starts asleep. The sim sleeps when nothing moves.
    Skin under the drape's edge isn't counted as exposed, and skin that starts under the drape stays under it.
- Skin damage (`skin.gdshader` + `WoundMap`): two painted textures (same texel size on every site, 128-512 px) drive cut grooves, burns (red halo to charred core),
  bruises (purple to yellow), stitches, blood pooling, marker ink, iodine and grime. Fat and muscle use `tissue_layer.gdshader`.
- Cavity blood rises as a glossy pool when bleeding inside, drops with suction.
- **Anatomy** (`anatomy` in `data/patient_sites.json`, `PatientBody.build_anatomy()`): the chest holds the lungs and
  the heart over the aorta under a rib cage and breastbone, the belly the liver, stomach and bowel over the kidneys and
  the aorta, under the lower rib margin. Arms, legs and the shoulder have their bones. Bones lie right under the muscle,
  organs are placed by how far under the muscle their top lies, so they stay inside on any patient. The cavity floor
  follows the skin. Organs in the way can be taken hold of with a clamp and moved aside; let go, they drift back.
  The heart beats with the pulse (still in asystole, a quiver in V-fib), the lungs swell with each breath.
- A skin flap pulled far comes loose from what's under it (the anchors give way past `TissueSim.ANCHOR_REACH`), so an
  H-shaped incision through the muscle folds back like a clamshell and shows the whole cavity.
- Screen grading (`post_grime.gdshader`): a restrained cool-green tint, vignette, a trace of grain and chromatic split.
  Sickness wobbles and blurs the view, passing out blacks it out.
  Blood thrown up right in front of your eyes lands on the view: a few drops that slide down and clear in a few seconds.
- Gloves pick up blood from the tool they hold, fingertips first, and the sink or a fresh pair cleans them.
  Bloody gloves slowly stain the scrubs, which stay stained for the rest of the surgery.
- Patient vitals are only on the bedside monitor, never on the player's screen.
  The monitor is laid out like a real one: sweeping ECG, pleth and breathing traces, numbers in their trace's color,
  alarms in the top bar and lab results along the bottom.
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
| manual_highlight | manual pages matching the patient are marked with a pointing hand |
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
- `starter_kit.cfg`: the tools every surgery starts with. With a nurse, a scenario's `starting_tools` and
  `random_tools` add only what she can't fetch; without one (sidewalk, ambulance) they add their whole kit.
- `drugs.cfg`: effects, onset, duration, flags, dangerous combinations, blood types, dose per kg.
- `events.cfg`: random escalation events, weights and cooldowns.
- `scoring.cfg`: points and stress per action, star thresholds.
- `consequences.cfg`: post-op report rules (infection, burst staples, lawsuits).
- `dialogue/patient_lines.cfg`: patient speech by trigger and age.
- `audio.cfg`: sound id to file.

## Mechanics

### In this draft

- Two-hand control, one active at a time, idle hand frozen mid-action. Effort levels, tilt and twist.
- Holding tissue anchors the hand; walking away tears it.
- Hand bumps between surgeons, lift to pass over. Jolts from seizures, coughs, potholes, pedestrians.
- Cuts with depth and speed (clean vs jagged) through skin, fat and muscle. Soft tissue sim: cuts gape, retraction widens, overpull tears.
- A blade pressed in without moving goes in as wide as itself, at its depth level. Moved along its edge it cuts on, also
  past the end of an opening it's already in. Over an opening it reaches down only at full depth: there it grates on a
  bone (which hurts through a local block) but stops short of an organ; it nicks an organ only by touching it.
- Per-segment closure: sew along the whole wound. Weak closures burst under strain.
- Bleeding per wound, blood pooling on skin and in the cavity, suction, gauze pressure, clamps, cautery, tourniquet.
- Drugs with onset/duration curves, direct vs IV routes, allergies, dangerous combinations, blood type matching.
- Cardiac arrest: V-fib, asystole, shocks, adrenaline windows, zapping a partner who's touching the patient.
- Seizures, malignant hyperthermia, diabetes drift, anesthesia wearing off, panicking awake patients.
- Organs you push or hold aside, targets you free by cutting, sawing, slow pulling or suction. Deep cuts reach bone.
- Dropped tools: floor makes them dirty, dropping into the cavity cuts something, heavy tools break fragile bones.
- Sterility tracking into the post-op report (infection, amputation).
- Nurse orders with a cooldown, blood panels with narrow/fast vs full/slow choices.
- Turning the patient as a shared quick time event, all surgeons on one side.
- Personal gauges: stress (pass out), sickness (vomit), breath (steady hands), sweat (slippery gloves, drips).
- Belt inventory, personal quirk items, drinking and wearing items, smoke breaks at the smoking spot.
- Manual with Divine knowledge highlights, patient card with red herrings.
- Scoring, stars, delayed consequences, codex unlocks, 23 scenarios.

### First feedback round

- **Figure it out**: no objectives, score or "done" toasts on screen, and the chart shows only the complaint (symptoms
  and history), never a plan. The manual is the one reference. Everything is still tracked internally; the debug
  setting shows the objectives, score and every scored action. Easy scenarios (1-2 stars) have no time limit.
- **Hands**: no height control. A hand rests the tool tip just above whatever is under it (skin, tray, organs or a
  target in an open cavity), measured on a collider made from the real body and gown meshes. The hand and the
  end of the forearm also keep clear of what's under them, so nothing sinks into a leg. Lift raises it over hands and tall tools, and while it holds onto something Lift pulls
  it up slowly. Hands stay within reach and hang at waist height when nothing reachable is below. Crouch reaches the
  floor and walks slowly. Zoom steps through three levels (hand motion scales with it for precision).
  The tool the empty hand would pick up is highlighted and named at the aim dot; Grab takes it in one press.
- **Grips**: every tool has a grip (`grip` in tools.cfg: pencil, rings, fist, flat) that places the glove on it and
  curls each finger. The glove then turns around the tool toward the forearm, only as far as a forearm turns
  (`SurgeonHand.MAX_ROLL`), so the back of the hand stays up. `data/grips.json` fits each tool model to the glove
  (moves it off the tool, opens or closes fingers) so no tool goes through the hand; `tests/fit_grips.tscn` makes it.
- **Tools on hard surfaces**: tools lie on the tray side by side at the start, a lowered tool only presses into skin,
  and every corner of a held tool and the glove clear tables, trays and tools lying there. Physics is Jolt.
  `tests/grip_gallery.tscn` renders every tool held, for checking.
- **Stations**: the nurse menu is grouped (`category` in tools.cfg) and deliveries land on a delivery tray.
  The defibrillator always waits on its own cart. Station cabinets are solid.
- **Floor dirt**: a tool that hits the floor is soiled and unsterile. Wash it at the sink, then sanitize it.
- **IV line**: the catheter pressed onto an arm starts a line; tubing then runs from the stand to the arm
  (`src/world/iv_line.gd`). It has to go into the forearm vein to work (`Patient.iv_in_vein`): beside it, it still
  sticks and the tubing runs to it, but nothing goes through. The last zoom step frames the catheter like a syringe.
  Where it went in, the catheter is taped down on the forearm (`IvDressing`, riding the forearm bone): its stub going
  into the skin toward the elbow, the hub with a colored cap and wings, a clear film over it, two strips of woven tape
  across the arm and the tubing taped along the arm before it hangs off to the stand.
  Walking into the line at full speed rips it out; crouch-walking steps over it.
- **IV drip** (`iv_drip` in tools.cfg): the bag on the stand is a fixed tool, 500 ml of fluid with room for 100 more.
  A syringe resting on top of it is in it: push a drug in and it runs down the line once the needle is out, if the
  line is in a vein (`ToolActions.drip()`); pull and the syringe draws the bag's fluid. Holding a saline or blood bag,
  the stand offers "Swap IV bag": the held bag replaces the hung one and runs in as a full dose.

### Controls rework

- **Look by default**: the mouse looks around like a regular first person game. Holding a hand's key (Q left, E right)
  moves that hand instead and makes it the active one. Hands turn and walk with the body unless they hold onto
  something (a gripped clamp or retractor), then they stay put.
- **One button per job** (`ToolActions.LEVEL_NAMES`, `TRIGGER_NAMES`): RMB picks up and puts down. Holding LMB uses the
  active tool: it lowers onto its spot and presses its single action, so clamps pinch and let go, the mallet strikes,
  the tourniquet goes on, a graft goes on, the defibrillator charges while held and shocks on release. Forceps holding
  a cotton pad wipe or dip it, and let it go when used in the air away from the dish.
  Tools with a range take an effort level 0-3 from the wheel (cut depth, stitch tension, heat, saw speed,
  suction, gauze pressure), 0 does nothing. Shift steps through three zoom levels, Alt lifts.
  A syringe has its own wheel instead: down pulls the plunger, up pushes it (see Vials and syringes).
- **Contextual aim**: a dot for point tools, a line along a blade's edge for blades. The edge is where the blade plane
  meets the skin, so rotating the tool (C/V) turns it. A blade only cuts moving along its edge; sideways it drags.
- **Controls shown for what you're doing**: the bottom right hint changes while a hand key is held or a tool is lowered.

### Starter kit and ordering

- **Same tray every time** (`data/starter_kit.cfg`): scalpel, forceps, kidney dish, cotton pads, iodine bottle with
  its dish, a 3, 10 and 50 ml syringe, IV catheter and saline bag. Everything else is ordered.
- **Tray layout** (`tray` in tools.cfg, `Room.TRAY_ZONES`): scalpel and forceps lie in a small steel tray, the cotton
  pads in a pile in another, the syringes side by side and the bottles and vials standing at one end. The rest fills
  the space left.
- **Nurse**: one order at a time, a 15 s cooldown after each delivery. A board over the bell shows the item on its way
  with a progress bar, then the cooldown.
- **Skin prep** (`ToolActions._wipe`): pour iodine from the bottle into the dish, pinch a cotton pad with forceps
  (or a hemostat), dip it, wipe the skin. A pad held in the glove or picked up off the floor contaminates the site.
  A dish soaks about four pads; a soaked pad runs dry after about 8 s of wiping.

### Vials and syringes

- **Drugs come in labelled vials** (`vial_*` in tools.cfg): the name is the label, with the strength per ml.
  Syringes (3, 10 and 50 ml) say only their size and whether they're full or empty, so players keep track of
  what's in which. The barrel is glass with a scale printed on one side, like a real syringe's: fine ticks (0.1 ml on
  the 3 ml, 0.2 on the 10, 1 on the 50) and numbered ml. The face of the black rubber stopper reads against it.
- **Plunger on the wheel** (`ToolActions.plunge()`): wheel down pulls the plunger out 1 ml a notch, wheel up pushes
  it in 1 ml, whether or not Use tool is held. The needle is in whatever its tip rests on or just over
  (`ToolActions.needle_target()`): over a vial or the dish it rests there, on the patient Use tool presses it in.
  - a vial, the kidney dish (holds 100 ml) or the IV drip: pulls its liquid, pushes into it. A full vial or bag takes
    no more.
  - a vein drawn on each forearm (`PatientBody.vein_at()`, not on an arm the site covers): pulls blood, which tints
    the liquid toward red by its share, pushes the drug in as an IV dose without a line (route `vein`).
  - skin, fat or muscle (the deepest layer a cut opens there, `PatientBody.layer_at()`): pushes a direct injection,
    pulls nothing and the plunger stays.
  - nothing: pulls air, pushes the liquid out in a squirt.
  A syringe holds ml plus an amount of each drug, so drawing from a second vial mixes (`ToolManager.transfer()`).
  Air sits at the needle end and goes out first. Pushing into the patient collects the dose; it's given when the
  needle comes out.
- **Needle view**: the last zoom step with a syringe or IV catheter in hand moves the camera beside it, side on and a
  little above, so the needle and what it's in (a vial, the dish, the bag, the arm) are in view. The hands fade to see
  through and roll a syringe about its length so the printed scale faces the camera; zooming out rolls it back.
- **Dosing**: the chart shows the patient's weight, the manual the dose per kg (`dose` in drugs.cfg).
  Between 0.7x and 1.4x the right dose works as the right dose; below or above it scales. Under half a dose it has
  only a faint effect and doesn't do its job (no objective, restart, antibiotic...). 2.5x and more is an overdose.
- **Weight**: rolled per age group, heavier with a heavy build quirk. The body model scales with the cube root of it.
- **Breaking**: a syringe that hits the floor shatters (`fragile` in tools.cfg).
- **Reading it**: holding Inspect (X) brings the tool in the active hand up in front of the eyes, across the view with
  its tick marks toward them. Liquid, air and plunger follow the ml exactly on every peer.

### Tourniquet

- Pressed onto an arm or a leg it wraps around the limb there: a band snug on the skin (`PatientBody.limb_ring()`
  measures the limb from inside with rays), and the hand lets go of it. Grabbing it again takes it off.

### Approved mechanics (in this build)

- **Pass the tool**: press Grab with a tool near your partner's empty hand to hand it over. Moving hands fumble it onto the floor.
- **Organ handling damage**: organs held out of place for more than a few seconds, or shoved hard, bruise (they darken) and ooze.
- **Suture tension**: the effort level sets stitch tension for the needle and paper clips. Loose leaks, tight can tear through.
- **Run modifiers** (`data/run_modifiers.cfg`): 1-2 per run, rolled in the lobby so both players see them before starting.
  Understaffed, expired drugs, bad wiring, med student, short supplies, blood shortage, broken heating, chart mix-up.
- **Chart mix-up** (a run modifier): the patient card shows a wrong blood type and allergy and misses a real condition,
  until the nurse brings the corrected copy partway through.
- **X-ray cart**: push it to the table, take an exposure, and a full-size film develops over six seconds.
  Viewed on a lightbox, it shows metal (bullets, knives, retained tools), bone, masses and trapped air.

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
- **Patient skin**: the body model draws the wound and fluid maps itself (`wound.gdshaderinc`, shared with the site
  skin shader), so cuts, burns, bruises, blood and iodine sit on the model. Only around cuts and skin a tool holds
  (TissueSim.region()) is the model cut away and replaced by the simulated skin, fat and muscle layers.
  `site_heights.json` (baked by raycasting the body) makes those layers hug the body. The cavity under them is a
  bowl that rises to just under the skin at the site's edges, so on a round limb it stays inside the body.
  The bake also lists grid points off the body: where the site overhangs it, or the body under the skin is too thin
  for skin, fat and muscle (the edge of a limb or the flank). Nothing of the site is drawn, carved or probed there,
  so it never sticks out past the body's outline. Flank points deeper than 6 cm under the site plane are still held
  at 6 cm, a known limit until the site becomes a proper surface patch.
- **Blood** (`src/visual/blood_flow.gd`): bleeding wounds well up into a puddle that grows with the blood lost and
  release rivulets from its edge that run downhill over the skin and stain it, drip off the body as droplets and pool
  on the table and the floor; strong bleeds spurt. An open wound fills the cavity first, then spills over.
  The shaders draw blood as a raised wet film: fresh red when thin, dark and glossy when thick, with a ragged edge
  whose rim catches the light.
- **Tool effects** (`src/visual/tool_effects.gd`, sent by the host through `Surgery.effect()`): cautery and lighter
  smoke, bone dust from the saw, blood thrown up by the mallet, a flash and sparks at the defibrillator paddles with
  the body jerking, a bead of blood where a needle or catheter goes in. Lasting marks (cuts, burns, stitches, ink,
  iodine, paddle marks) go into the wound map. Tools working in blood come away bloody at the tip, gauze soaks
  through (`toon.gdshader` coat); the sink washes it off. The IV catheter gets a film dressing.
- **Animation** is procedural and driven by synced game state, so it matches on every peer:
  - Patient (`patient_animator.gd`, bones posed through `bone_rig.gd` in model-space axes): breathing at the
    respiration rate (the trunk and surgical site rise together), eyes open when conscious,
    jaw moves while talking, head tracks and flinches with pain, panic flails, seizures shake every joint.
  - Surgeon: walk cycle from movement speed, collapse when passed out, head tilt from camera pitch,
    two-bone IK arms, glove finger bones relax, wrap around a held tool and squeeze while using it.
    The glove's cuff has its own bone aimed down the forearm, so a bent wrist stretches the glove over the sleeve.
  - Tools (`tool_animator.gd`): jaws open and close, plungers push, stapler triggers squeeze, saw blades oscillate,
    lighter flame and cautery tip light up, defibrillator charge light blinks.
- **Sounds**: 37 effects synthesized from noise, oscillators, filters and formants (tissue, tools, room tone loops,
  surgeon, patient groans/screams/breathing). The monitor beep is generated live.
  Patient speech is subtitles; optional recorded lines can be dropped into `assets/audio/voice/`.

## Known limits of this iteration

- Grips are four styles, not a pose per tool; a tool passes between the fingers rather than touching them exactly.
- Physics pushing of organs runs on the host only; clients see synced positions.
- Tilt and twist change tool orientation and tip position, but no target yet requires a specific twist.
- Late joining mid-surgery isn't supported. Both players join in the lobby.
- Blood rivulets, drips and pools are drawn by each peer on its own, so they differ slightly between players.
