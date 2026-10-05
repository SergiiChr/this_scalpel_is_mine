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
- **Tissue topology is broadcast the same way.** Cuts, stitches, bursts, snapped springs and skin taken off are reliable RPCs,
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
    6 mm square (`TissueSim.CELL`), wider on a big site so it has no more than `MAX_CELLS` (a belly's are about
    9.5 mm): a whole belly folded open moves every particle at once, and that has to fit a frame. Only an active
    window is simulated: around cuts, grips and skin that moved, plus a margin of still skin. It only grows while the
    skin moves and is picked afresh once it sleeps.
    Cutting severs springs, so an incision gapes on its own; forceps and the retractor pin particles and stretch it
    further, the Gelpi retractor's two jaws each pin the edge on their side and move them apart (`TissueSim.grip_beside()`).
  - Each severed spring remembers how deep the cut went (skin, fat or muscle), where the blade crossed it and which
    way the cut ran. The edges of a cut are drawn back square to it, more the deeper it goes: skin gapes a few
    millimeters, fat more, cut muscle retracts hard. The pull tapers off toward the cut's ends (where each stroke
    starts and where the blade is), so a cut opens like a lens, closed at both ends, like a zipper behind the blade.
  - Skin, fat and muscle are three meshes rebuilt from the sim, only where the simulated skin replaces the body (the
    region). A layer cut through is split exactly where the blade crossed each spring, not along the grid: each side
    keeps its part of the triangle and moves with it. Walls run down each lip through the layer's thickness (the
    skin's cut face in its own tone, yellow fat, red muscle), so a cut has depth. The simulated skin doesn't paint the
    wound map's cut groove: its lips are skin right up to the split: a skin cut shows the fat (or the muscle where there's no fat),
    a deeper one the muscle, a full depth cut the bone or organs under it. The meshes rebuild on the frame after the
    sim steps, so the two costs don't land on one frame. Which triangles there are and how they split is planned only
    when the cuts or the region change; while the skin just moves, only the vertices move.
  - The layers are drawn on the body model, not where the sim settled (tension pulls the sheet a few millimeters off a
    round limb, centimeters off the belly's flanks): each grid point is laid onto the model once, with the model's own
    smooth normal there, and drawn as far from it as the sim moved it since. Pores and grime are laid out in the
    model's space, like the body's. The skin is moved 1 mm toward the camera along the view ray, so it wins over the
    model where they overlap without a visible step.
  - Fat is per site (`fat` in `patient_sites.json`, 12 mm when a site doesn't say): none on the forearm, where a cut
    deeper than the skin goes into the muscle.
  - Overstretched springs snap into a tear (host only), and clients snap the same spring by its index. A spring snaps
    only if the one going on from it the same way is at least halfway there too: skin tears where it's overstretched
    over a length, not where one short spring of the grid takes a jump. Springs in the site's outermost strip (under
    the drape's frame) or to skin hanging off the body never snap; springs stretched at rest (the edge of a round limb)
    break only well past that.
    Stitches are extra springs across the cut, their length is the tension. Thread is stiffer than skin (solved more
    often). A stitch closes a few millimeters of the cut. A cut counts as closed only where its edges meet: a loose
    stitch leaves a gap that stays open and bleeds.
  - The needle sews a running suture (`TissueSim.thread_anchor()`, `Patient.place_suture_anchor()`): each click makes a
    hole and a spring from the last one, the wheel sets every span's length at once (`TissueSim.THREAD_CLOSED` and
    the rest, per layer), and a long hold ties it off. The thread closes the wound bins it crosses and halfway to the
    next crossing, over whatever other closures left there, and raises the pressed edges into a lip
    (`TissueSim.suture_pucker()`). Tied off, it joins every severed edge it holds (`TissueSim.stitch_path()`), closes
    the muscle or the fat under it (`TissueSim.close_layer()`). Pulled past `THREAD_TEAR`, or shut over open
    muscle, it tears through (`TissueSim.snap_thread()`).
  - Tools and hands touch the skin as it's deformed now (`TissueSim.skin_height()`), not the body's rest shape, so a
    lifted fold is where it's drawn. Over the site a hand rests on that skin, not on the gown's or the site's colliders.
  - A grip holds the skin within 10 mm of its jaws at its distance (it can still turn with a flap folded back) and
    drags a patch around it along (never across a cut, not even round its ends: that's the other edge, which would
    go along and the cut wouldn't open), so pulls spread and the skin stretches visibly over several centimeters
    before it tears. Everything that moved is shown simulated. Grips held still let the sim sleep.
  - A cut's edge lifted off the body (a flap folded back) isn't drawn back from the cut any more.
  - Cut muscle retracts and pulls the edges further apart. It's sewn from inside the wound (`TissueSim.muscle_stitch()`,
    `Patient.close_muscle_at()`, a needle's thread started on the muscle), through a stab or bullet hole too small to
    reach into, and skin won't close over open muscle: it refuses, or a tight stitch tears through.
  - Skin cut out all round (a circle through the skin) is a piece (`TissueSim.piece_of()`): pinched with forceps and
    lifted 1 cm, it comes off whole (`TissueSim.excise()`). The skin layer has a hole there, the fat (or the muscle,
    where there's no fat) shows, and the forceps hold the piece as a skin graft with one use: pressed onto a cleaned
    burn it goes on, pressed in the air it's let go.
  - The skin settles under its own tension when it's built, so it starts asleep. The sim sleeps when nothing moves.
    Skin under the drape's edge isn't counted as exposed, and skin that starts under the drape stays under it, unless
    it was cut free there (a flap cut under the drape's frame takes it along).
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
  Sickness wobbles and blurs the view, passing out blacks it out, a sedative blurs it and too much darkens it.
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
`sites` limits a patient quirk to scenarios on those surgical sites (an aneurysm only where a great vessel runs).

Patient effect keys:

| Key | Meaning |
|---|---|
| glucose_drift | mmol/L per second of rising blood sugar |
| seizure_chance | seizures per second |
| heal_rate | wound closure per second (every wound) |
| bleed_mult | bleeding multiplier |
| clot_risk | cardiac arrest chance per second unless heparin is active |
| fragile_vessels | systolic pressure above 140 mmHg can burst a deep vessel under the site |
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
| stress_floor | stress never drains below this (shaky quirks), stacked up to 0.9 |
| tremor_mult | multiplier on all hand shaking (0 = Steady hands, never shakes) |
| weight_kg | added to the surgeon's 80 kg (doses given to them scale with it) |
| bump_resist | 0..1 less likely to drop tools when jolted |
| belt_slots | belt capacity change (default 4) |
| items | personal tool ids spawned on the belt |
| manual_highlight | manual pages matching the patient are marked with a pointing hand |
| heavy_tools_blocked | small hands can't use heavy tools |
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
`disabled=true` keeps a scenario out of the menu and lobby while its positive flow test is broken; a comment beside it
names the test.

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
- `manual/NN_id.txt`: in-game manual pages. A folder named like a page (`manual/17_conditions/`) holds its sub-pages:
  one entry per chronic condition (patient quirk with a chart line), tagged with the quirk id or `id.variant` for
  Divine knowledge. General pages point to these entries instead of repeating them.
  Game numbers in pages are `{expression}`s worked out at load from class constants, `drug`, `tool` and `quirk`
  (see `ManualPage`), so doses, thresholds and chances follow the game. A data test rejects numbers with units
  written by hand.
- `audio.cfg`: sound id to file.

## Mechanics

### In this draft

- Two-hand control, one active at a time, idle hand frozen mid-action. Effort levels. Holding MMB the mouse turns the
  held tool about the wrist (tilt up and down, turn left and right, `Surgeon.aim_tool()`): the wrist and forearm stay
  put, the tip follows the mouse and rises off what it rested on, and settles back down once MMB is let go. C/V roll
  it about its length.
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
  Arrests follow from the patient's state (blood loss, low pressure, racing pulse, fever, sugar out of range, swelling,
  clots, heart quirks); there is no random arrest event. Scripted arrests (heart attack) always happen.
- General anesthesia holds for the whole surgery once given, side effects included (propofol and gas keep the pressure
  about 15-20 mmHg down, the manual says how to manage it). A repeat dose only tops it up. It wears off only with the
  anesthesia resistant quirk or a scripted wake up (awake craniotomy).
- Random events only disturb the surgeons (a pothole, a cough, a bump, flickering lights with the power trouble
  modifier). Nothing random happens to the patient that the chart, the monitor and the manual don't explain.
- Seizures, malignant hyperthermia, diabetes drift, panicking awake patients.
- Organs you push or hold aside, targets you free by cutting, sawing, slow pulling or suction. Deep cuts reach bone.
- Dropped tools: floor makes them dirty, dropping into the cavity cuts something, heavy tools break fragile bones.
- Sterility tracking into the post-op report (infection, amputation).
- Nurse orders in batches of up to five with a cooldown, blood panels with narrow/fast vs full/slow choices.
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
  floor and walks slowly. Zoom toggles between two levels (hand motion scales with the magnification for precision, so
  the hand crosses the screen as fast at both); the closer one makes the hands see-through. The hands start turned in
  toward the middle, so each tool points across in front of the eyes, beside its hand.
  The tool the empty hand would pick up is highlighted and named at the aim dot; Grab takes it in one press.
- **Grips**: every tool has a grip (`grip` in tools.cfg: pencil, rings, fist, flat) that places the glove on it and
  curls each finger. The glove then turns around the tool toward the forearm, only as far as a forearm turns
  (`SurgeonHand.MAX_ROLL`), so the back of the hand stays up. `data/grips.json` fits each tool model to the glove
  (moves it off the tool, opens or closes fingers) so no tool goes through the hand; `tests/support/fit_grips.tscn` makes it.
- **Tools on hard surfaces**: tools lie on the tray side by side at the start, a lowered tool only presses into skin,
  and every corner of a held tool and the glove clear tables, trays and tools lying there. Physics is Jolt.
  `tests/support/grip_gallery.tscn` renders every tool held, for checking.
- **Stations**: the nurse menu is a shop: categories (`category` in tools.cfg), each a list of items with [-] count
  [+], and a cart. Deliveries land side by side on a delivery tray.
  The defibrillator always waits on its own cart. Station cabinets are solid.
- **Floor dirt**: a tool that hits the floor is soiled and unsterile. Wash it at the sink, then sanitize it.
- **IV line**: the catheter pressed onto an arm starts a line; tubing then runs from the stand to the arm
  (`src/world/iv_line.gd`). It has to go into the forearm vein to work (`Patient.iv_in_vein`): beside it, it still
  sticks and the tubing runs to it, but nothing goes through. The last zoom step fades the hands as for a syringe.
  Where it went in, the catheter is taped down on the forearm (`IvDressing`, riding the forearm bone): its stub going
  into the skin toward the elbow, the hub with a colored cap and wings, a clear film over it, two strips of woven tape
  across the arm and the tubing taped along the arm before it hangs off to the stand.
  Walking into the line at full speed rips it out and the catheter drops on the floor at the walker's feet (soiled,
  wash and sanitize it to use it again); crouch-walking steps over it.
- **IV drip** (`iv_drip` in tools.cfg): the bag on the stand is a fixed tool, 500 ml of fluid with room for 100 more.
  A syringe brought over the bag snaps its needle into the bag's middle, straight into the face on the hand's side
  and a little upward, the way the forearm rises to it (`Surgeon._snap_spot()`); moved on, it comes out and the hand
  holds the syringe as before. While it's in: push a drug in and it starts down the line at once, ahead of the bag's
  own fluid, at 2 ml a second (`ToolActions.DRIP_RATE`), if the line is in a vein (`ToolActions.drip()`). Each bit is
  given as it reaches the patient; debug mode tells each ml and the total so far. Pull and the syringe draws by the
  port: what was pushed in and hasn't run yet first, then the bag's fluid. Holding a saline or blood bag,
  the stand offers "Swap IV bag": the held bag replaces the hung one and runs in as a full dose.

### Controls rework

- **Look by default**: the mouse looks around like a regular first person game. Holding a hand's key (Q left, E right)
  moves that hand instead and makes it the active one. Hands turn and walk with the body unless they hold onto
  something (a gripped clamp or retractor), then they stay put.
- **One button per job** (`ToolActions.LEVEL_NAMES`, `TRIGGER_NAMES`): RMB picks up and puts down. Holding LMB uses the
  active tool: it lowers onto its spot and presses its single action, so clamps pinch and let go, the mallet strikes,
  the tourniquet goes on, a graft goes on, the defibrillator charges while held and shocks on release. Forceps holding
  a cotton pad wipe or dip it, and let it go when used in the air away from the dish. Forceps holding a graft taken
  from the skin put it on a cleaned burn, or let it go in the air.
  The Rotate keys roll a held tool about its length: a scalpel's blade turns with it, to follow a curve.
  Tools with a range take an effort level 0-3 from the wheel (cut depth, stitch tension, heat, saw speed,
  suction, gauze pressure), 0 does nothing. Shift toggles between two zoom levels, Alt lifts.
  A syringe has its own wheel instead: down pulls the plunger, up pushes it (see Vials and syringes). So has the
  needle: down tightens its thread, up loosens it; a click stitches and a hold ties off (see the running suture). And
  the Gelpi retractor: up opens it, down closes it (see Gelpi retractor).
- **Contextual aim**: shown on whatever is right under the tool's tip, where Use tool brings it down, so it's
  accurate at any angle (`Surgeon.aim_point()`). A dot for point tools, a line along a blade's edge for blades, a < and a > at a Gelpi
  retractor's tips. The edge is where the blade plane meets the skin, so rolling the tool (C/V) or turning it (MMB)
  turns it. A blade only cuts moving along its edge; sideways it drags.
- **Controls shown for what you're doing**: the bottom right hint changes while a hand key is held or a tool is lowered.

### Starter kit and ordering

- **Same tray every time** (`data/starter_kit.cfg`): scalpel, forceps, kidney dish, cotton pads, iodine bottle with
  its dish, a 3, 10 and 50 ml syringe, IV catheter and saline bag. Everything else is ordered.
- **Tray layout** (`tray` in tools.cfg, `Room.TRAY_ZONES`): scalpel and forceps lie in a small steel tray, the cotton
  pads in a pile in another, the syringes side by side and the bottles and vials standing at one end. The rest fills
  the space left.
- **Nurse**: one order at a time, a cart of up to five items (the same one twice too) fetched together, so it takes
  as long as its slowest item. A 15 s cooldown after each delivery from the sixth on (the first five come without).
  Every drug is under one Drugs group. A board over the bell shows the batch on its way
  with a progress bar, then the cooldown.
- **Skin prep** (`ToolActions._wipe`): pour iodine from the bottle into a dish (20 ml a second while Use tool is
  held), pinch a cotton pad with forceps (or a hemostat), dip it, wipe the skin. A pad held in the glove or picked up
  off the floor contaminates the site. A pad soaks up 10 ml (`ToolActions.PAD_ML`), so the 40 ml iodine dish soaks
  four; a soaked pad runs dry after about 8 s of wiping.
- **Dishes** (`ToolDef.is_dish()`: a volume and no action of its own, the iodine dish and the kidney dish) all work
  the same: bottles pour into them, syringes squirt into them and draw from them, pads dip into iodine in them
  (`ToolManager.nearest_dish()`). They show their liquid by its ml, tinted toward iodine and blood by their share.

### Vials and syringes

- **Drugs come in labelled vials** (`vial_*` in tools.cfg): the name is the label, with the strength per ml.
  Syringes (3, 10 and 50 ml) say only their size and whether they're full or empty, so players keep track of
  what's in which. The barrel is glass with a scale printed on one side, like a real syringe's: fine ticks (0.1 ml on
  the 3 ml, 0.2 on the 10, 1 on the 50) and numbered ml. The face of the black rubber stopper reads against it.
- **Snapping to a vial**: a syringe whose tip passes over a vial's cap snaps its needle in through the cap along the
  vial (down into one standing, level into one lying), before Use tool is pressed. Only a cap that faces the surgeon
  snaps: up, or lying, pointing their way. Vials and bottles come from the nurse standing, cap up.
- **Standing a bottle up**: holding Grab a second with a bottle in hand stands it upright where it's held, on whatever is
  under it (`ToolManager.standing_on()`). A click puts it down like any other tool. It lets go a little further out than it snaps in, so passing over doesn't hold it for
  long. Snapping in and out (onto the IV bag too) eases over 0.25 s, the needle gliding over rather than jumping.
- **Plunger on the wheel** (`ToolActions.plunge()`): wheel down pulls the plunger out 1 ml a notch, wheel up pushes
  it in 1 ml, whether or not Use tool is held. The needle is in whatever its tip rests on or just over
  (`ToolActions.needle_target()`): over a vial or the dish it rests there, on the patient Use tool presses it in.
  - a vial, a dish (the kidney dish holds 100 ml, the iodine dish 40) or the IV drip: pulls its liquid, pushes into it. A full vial or bag takes
    no more.
  - a vein drawn on each forearm (`PatientBody.vein_at()`, not on an arm the site covers): pulls blood, which tints
    the liquid toward red by its share, pushes the drug in as an IV dose without a line (route `vein`).
  - skin, fat or muscle (the deepest layer a cut opens there, `PatientBody.layer_at()`): pushes a direct injection,
    pulls nothing and the plunger stays.
  - a surgeon's glove (the other hand of the one holding it, or a partner's) or a partner's body: the needle rests
    on the back of a glove, wrist to fingertips, like on skin. Pushes a dose into that surgeon (`Surgery.dose_surgeon()`, route `surgeon:<peer>`),
    pulls nothing. A glove comes before the patient under it, a body after.
  - nothing: pulls air, pushes the liquid out in a squirt.
  A syringe holds ml plus an amount of each drug, so drawing from a second vial mixes (`ToolManager.transfer()`).
  Air sits at the needle end and goes out first. Pushing into the patient or a surgeon gives what's pushed as it goes
  in, one notch at a time.
- **Needle in the patient sticks**: with Use tool held and the needle in a vein or tissue, its tip stays exactly where
  it went in (no tremor, no lift). Moving the mouse toward or away from the body tilts the syringe about the tip, the
  hand swinging round it (`Surgeon._bend_needle()`). What the tilt can't follow (sideways, or past the tilt range)
  stretches the skin by a fifth of the motion; stretched 1.5 cm, or walked away from out of reach, the needle tears
  out: a short scratch, a bead of blood and pain (`Patient.needle_tear()`). It then moves freely until Use tool is
  let go. Releasing Use tool withdraws it immediately and leaves a visual blood bead exactly at the puncture, without
  adding pain or a scratch (`ToolManager.request_needle_withdrawal()`). Moving the hand afterward cannot tear it out.
- **Held facing you**: picked up, a syringe is held ready to inject (grip `syringe`): the index and middle fingers over
  its finger grip, the thumb on the plunger, following it in and out (`SurgeonHand._reach_plunger()`). It points a
  little down and in toward the body's middle, the hand off to its outer side, with the printed scale turned toward
  the eyes, so it doesn't need turning to be read. Let go, the hand holds things the way it did before. Moved
  about, it keeps turning the scale to the eyes, so C/V don't roll it (and the controls shown leave them out).
- **Needle view**: the last zoom step fades the hands to see through whatever they hold, so the needle and where it
  goes show. The camera stays at the eyes, so aiming moves the hand the way it always does.
  Use tool with a syringe zooms all the way in on its own and back out when let go.
  Once a syringe's needle is in (Use tool held in a vial, the dish, the bag, the patient or a glove), the camera moves
  beside it, side on and a little above, so the needle and what it's in are in view, and the hand rolls the syringe
  about its length so the printed scale faces the camera. There the mouse moves the hand as seen from the camera:
  right on screen is right, up is away from it. Use tool let go, the camera goes back and the scale turns back to the
  eyes.
- **Dosing**: the chart shows the patient's weight, the manual the dose per kg (`dose` in drugs.cfg).
  Doses add up (`DrugLevels`, for the patient and the surgeons alike): every injection goes into a depot that soaks
  into the blood over the route's onset (a direct injection 0.4x the drug's onset, a vein or the IV line 1.5x), and the
  level in the blood drops by one right dose every `duration` seconds, so ten 1 ml shots work like one 10 ml shot and
  twice the dose lasts twice as long. Between 0.7x and 1.4x the right dose in the blood works as the right dose; below
  or above it scales. Under half a dose it has only a faint effect and doesn't do its job (no objective, restart,
  antibiotic...); reaching half a dose it does. 2.5x taken in (in the blood or still soaking in) is an overdose; bags
  of fluid or blood have no dose to overdo. General anesthesia holds at the right dose (topped up by the anesthetist):
  more wears off as usual, and a patient who burns through it loses it all. A lethal drug that worked ends it some
  time later, however fast it wears off.
- **Weight**: rolled per age group, heavier with a heavy build quirk. The body model scales with the cube root of it.
  Surgeons weigh 80 kg (small hands 60), shown in the lobby under their name; doses given to them use it.
- **Breaking**: a syringe that hits the floor shatters (`fragile` in tools.cfg).
- **Reading it**: holding Inspect (X) brings the tool in the active hand up in front of the eyes, across the view with
  its tick marks toward them. Liquid, air and plunger follow the ml exactly on every peer.

### Stress, tremor and sedation (`SurgeonStatus`)

- **Stress is the tremor**: under 30% the tool stays still and only the glove twitches now and then; up to 60% a
  light shake reaches the tool; above it a plain one that grows with stress. Quirks only change how fast stress
  builds (`stress_mult`) and how low it drains (`stress_floor`): Shaky hands 65%, Alcoholic 35% (none while a sip
  works), a coffee +15% while it works. Cold (run modifier) shakes on its own. Holding breath steadies all of it,
  Steady hands takes it all away.
- **Diazepam for a surgeon** (flag `benzo`): injected into a hand (their own other hand or a partner's) or a
  partner's body, dosed by the surgeon's weight. At the right dose it stops stress shaking (not the cold) for its
  five minutes, blurs the view (mip level 1) and delays the mouse's hand moves by 100 ms (looking around isn't);
  afterimages trail the gloves. Past 1.4 times the dose the view darkens toward the edges and the delay grows, up
  to 300 ms. Twice the dose knocks the surgeon out for five minutes: they tip over sideways onto the side with more
  floor, lie facing the table with the view 80% dark at the edges, can't do anything and moan now and then
  (`surgeon_moan`, heard by everyone). Flumazenil (`reverse_benzo`) brings them round and ends the diazepam;
  adrenaline (`stimulant`) gets them up only while it lasts, and if enough diazepam is still working they go down
  again. On the patient, flumazenil reverses diazepam too.

### Gelpi retractor

- A self-retaining spreader (`action="spread"`), beside the plain retractor that pulls one edge like forceps: ring
  handles with a ratchet rising to a box joint and two long arms, each ending in a point bent down under it. It's held
  tipped toward the skin, its points down (`SurgeonHand.SPREADER_TILT`), and lies along a cut with its jaws across
  it. The aim shows a < and a > where its tips are, square to its length (`ToolActions.spread_tips()`); C/V swing it
  about the upright to turn them across a cut (`SurgeonHand.spreads`). The wheel opens and closes it, in the hand or
  set, from 1.2 to 8 cm between the tips (`ToolActions.SPREAD_RANGE`); the arms swing about the joint to match
  (`ToolAnimator.open_to()`), the handles stay in the fingers.
- Use tool on a cut sets it: each jaw takes hold of the skin on its own side of the middle, so set right over a cut
  each holds one edge (`Patient.set_spreader()`). It goes down lying flatter along the skin, its points into the cut
  as deep as the cut goes (through the skin, the fat or the muscle, at most `ToolActions.SPREAD_REACH`), so how far
  the arms sink shows how deep the cut is (`SurgicalTool.dig_to()`). Pressed on skin with no cut between its tips it
  doesn't set: the hand bounces off and comes back down (`SurgeonHand.bounce()`). Opened or closed, each edge moves
  half the change (`Patient.open_spreader()`), and the cut opens like the skin lets it: opened too far it tears at the
  ends. Use tool again takes it out.
- Set, it stays where it went in: the hand holding it goes to it and doesn't shake (`Surgeon._hold_in_wound()`).
  Put down, or walked away from, it stays set in the wound (self-retaining). Picked up again, the wheel works on it.
  Left set, it doesn't keep hands or tools off the cut: a blade cuts the layers under the skin between its jaws.

### Retractor

- Use tool hooks the skin where it's pressed, and moving the hand pulls it that way, like forceps. Grab lets go of
  the handle but not the skin (self-retaining): the retractor lies down along the body from the hook, pointing away
  from where it took hold, resting on the highest skin under it, and keeps the skin pulled (`ToolManager.lying_from_hold()`).
  Taken back, the hand goes to where the hook holds, so the skin isn't dragged; Use tool unhooks it.
- It hooks only skin it's pressed onto, at a cut's edge (`ToolActions.SKIN_HOOKS`): never a target, vessel or organ
  under it, and not down in the opening, where it would drag the skin far above down and tear it.
- Anything left holding onto the patient (a retractor, a hemostat, a Gelpi retractor) has no collider: hands and
  tools reach past it (`SurgicalTool.set_state()`).

### Tourniquet

- Pressed onto an arm or a leg it wraps around the limb there: a band snug on the skin (`PatientBody.limb_ring()`
  measures the limb from inside with rays), and the hand lets go of it. Grabbing it again takes it off.

### Approved mechanics (in this build)

- **Pass the tool**: press Grab with a tool near your partner's empty hand to hand it over. Moving hands fumble it onto the floor.
- **Organ handling damage**: organs held out of place for more than a few seconds, or shoved hard, bruise (they darken) and ooze.
- **Suture tension**: the needle's wheel sets its thread's tension, the effort level sets paper clips'. Loose leaks,
  tight can tear through.
- **Run modifiers** (`data/run_modifiers.cfg`): 1-2 per run, rolled in the lobby so both players see them before starting.
  Understaffed, expired drugs, bad wiring, med student, short supplies, blood shortage, broken heating, chart mix-up.
- **Chart mix-up** (a run modifier): the patient card shows a wrong blood type and allergy and misses a real condition,
  until the nurse brings the corrected copy partway through.
- **Blood pressure**: panic and stimulants (adrenaline, cocaine, ketamine) raise systolic pressure. Above 140 mmHg,
  and while heparin acts, sutures, staples, tape and gauze leak; cautery and clamps hold. Patients with fragile vessels
  (an aneurysm) can burst a deep vessel under the site above the same pressure.
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
  The site's skin heights are measured on the body model when the patient is built (rays down the site's normal,
  `PatientBody._measure_site()`), so those layers hug the body as it is, whatever the model becomes. Every peer
  measures the same model the same way, so every player sees the same site. The cavity under them is a bowl that
  rises to just under the skin at the site's edges, so on a round limb it stays inside the body.
  The measuring also finds grid points off the body: where the site overhangs it (or a ray goes through a hole the
  eyes fill), or the body under the skin is too thin for skin, fat and muscle (the edge of a limb or the flank).
  Nothing of the site is drawn, carved or probed there, so it never sticks out past the body's outline.
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
  - Surgeon: walk cycle from movement speed, collapse when passed out, lying on the side when knocked out, head tilt
    from camera pitch,
    two-bone IK arms, glove finger bones relax, wrap around a held tool and squeeze while using it.
    The glove's cuff has its own bone aimed down the forearm, so a bent wrist stretches the glove over the sleeve.
  - Tools (`tool_animator.gd`): jaws open and close, plungers push, stapler triggers squeeze, saw blades oscillate,
    lighter flame and cautery tip light up, defibrillator charge light blinks.
- **Sounds**: 44 effects synthesized from noise, oscillators, filters and formants (tissue, tools, room tone loops,
  surgeon coughs and moans, patient groans/screams/breathing). The monitor beep is generated live.
  Patient speech is subtitles; optional recorded lines can be dropped into `assets/audio/voice/`.

## Known limits of this iteration

- Grips are four styles, not a pose per tool; a tool passes between the fingers rather than touching them exactly.
- Physics pushing of organs runs on the host only; clients see synced positions.
- Tilt and twist change tool orientation and tip position, but no target yet requires a specific twist.
- Late joining mid-surgery isn't supported. Both players join in the lobby.
- Blood rivulets, drips and pools are drawn by each peer on its own, so they differ slightly between players.
