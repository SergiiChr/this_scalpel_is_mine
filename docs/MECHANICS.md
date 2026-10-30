# Mechanics

How the game plays and looks: what players experience, and the rules behind it.
Rule numbers here must match `data/` and the code; the data and code win.

## Look

- Palette (`Materials`): surgical green for walls, gowns and drapes, cool fluorescent white for every room light, a
  warmer white only for the surgical lamp. The two surgeons wear green and ceil blue scrubs. Colors outside that family
  are for things that should stand out: the red crash cart, blood, drug labels. The operating room alone is a clean
  modern theatre: white tiles up to a steel rail at 1 m and blue paint above, a speckled gray floor, a ceiling of 60 cm
  panels, blue drapes on the furniture, white laminate instead of wood. Windows with blinds, a door and an outlet panel
  are painted on the walls (`room.gdshaderinc`), decoration only. Its walls, floor and ceiling carry almost no grime.
  Tools and people keep their colors.
- Lighting: a ceiling panel light over the table lights the room from above, the surgical lamp's spot focuses on the
  site, dim tubes fill the corners. The theatre has four flush LED panels in the ceiling instead, each with a light
  under it, in a bluer white, brighter, and neutral ambient light instead of green. The ambulance also uses neutral
  light, with a flush ceiling fixture and no hanging surgical-lamp model. Outside, the streetlight does the ceiling
  light's job. The flicker event dims every room light but the surgical lamp. Forward+ adds SDFGI with four cascades and
  10 cm cells; indoor rooms ignore sky lighting. Only the fixed room shell contributes to the static GI field. Gameplay
  meshes, including later tool deliveries and new tissue, receive indirect light but do not contribute static occlusion
  or emission. Direct lights and their shadows stay live. Mobile and Compatibility keep a plain ambient fill.

- Shading (`toon.gdshader`): smooth diffuse, soft specular, rim light, procedural grime. Every model material belongs to
  a family (`Materials.Families`, by material name): skin (light wraps past the terminator with a red tint), glove
  rubber, cloth (no highlight, soft sheen), metal (tinted highlight, a fake ceiling/floor reflection), plastic and wet
  tissue. The model's color, roughness, metallic and texture maps are kept. Grime rides on the model; only walls and
  floors keep theirs fixed in the world. Room surfaces skip specular and rim.
- Fine relief per family (`Materials.Detail`): skin pores, glove creases, cloth weave and folds, brushed steel. It only
  tilts the normal, so silhouettes and collisions are the model's own.
- Surgical drape (`Drape.cs`, operating room only): a sheet over the torso and legs laid from the body's rest mesh, 1.2
  cm off the skin so breathing never pushes the body through it, rising with the trunk. It falls off the flanks and
  between the legs at a fixed slope and its long sides hang down to the table, over most of the table past the legs and
  a little short of the arms beside the trunk (they may lift). Its opening frames the site and covers the site's edge.
  It hides while the patient is turned away from the site; hands rest on it. A hand holding the patient through it
  presses it down onto the skin under the hand, rising back round it (`Drape.Press()`).
- Organs and cavity walls (`flesh.gdshader`) show branching vessels and mottling.
- Ink outline via inverted hull (`outline.gdshader`): about 1.4 px wide at any distance, capped by the part's size, so a
  blade gets a hairline and furniture a full line.
- Surgical site tissue: the skin is a separate soft layer over fat and muscle, a grid of particles joined by springs
  under tension (`TissueSim`), drawn as three layers split exactly where the blade went (`PatientBody`). Cuts gape on
  their own, deeper ones further; grips stretch the skin over centimeters before it tears; closures count only where the
  edges actually meet. How it's simulated and drawn is documented on those classes.
- A hand holding skin is held by it: pulled past what the skin gives (`TissueSim.GripStrain`, the longest spring at the
  held skin over its rest length) the grip stops where the skin does and the hand comes back to it
  (`TissueSim.Restrain()`, `Surgery.HoldHandBack()`), so skin isn't dragged across the site like rubber. Pulled on from
  there, it peels a cut's lip up off what's under it instead (`TissueSim.PeelLift`): the skin and its fat rise, the
  muscle stays down (`PatientBody.LayerPoint()`), so a tight wound held up shows its muscle edge for the needle. A flap
  cut free swings as far as its hinge lets it. Skin still tears where a tool forces it (a surgeon walking away holding
  on, a thread pulled too tight).
- Skin damage (`skin.gdshader` + `WoundMap`): two painted textures (same texel size on every site, 128-512 px) drive cut
  grooves, burns (red halo to charred core), bruises (purple to yellow), stitches, blood pooling, marker ink, iodine and
  grime. Fat and muscle use `tissue_layer.gdshader`.
- Cavity blood rises as a glossy pool when bleeding inside, drops with suction.
- **Anatomy** (`anatomy` in `data/patient_sites.json`, `PatientBody.BuildAnatomy()`): the chest holds the lungs and the
  heart over the aorta under a rib cage and breastbone, the belly the liver, stomach and bowel over the kidneys and the
  aorta, under the lower rib margin. Bones lie right under the muscle, organs are placed by how far under the muscle
  their top lies, so they stay inside on any patient. The cavity floor follows the skin. Arms, legs and the shoulder
  have the body model's own skeleton for bones, the one X-ray films show (`PatientBody.MeasureLimbBones()`): a copy of
  the part under the site, found with rays down the site. A limb is muscle down to its bones, not a hollow: the muscle
  reaches to just over them in the middle of the site, the floor lies just under their middle along the limb, so they
  lie half sunk in it. Organs in the way can be taken hold of with a clamp and moved aside; let go, they drift back. The
  heart beats with the pulse (still in asystole, a quiver in V-fib), the lungs swell with each breath.
- A skin flap pulled far comes loose from what's under it (the anchors give way past `TissueSim.AnchorReach`), so an
  H-shaped incision through the muscle folds back like a clamshell and shows the whole cavity.
- Screen grading (`post_grime.gdshader`): a restrained cool-green tint, vignette, a trace of grain and chromatic split.
  Sickness wobbles and blurs the view, passing out blacks it out, a sedative blurs it and too much darkens it. Blood
  thrown up right in front of your eyes lands on the view: a few drops that slide down and clear in a few seconds.
- Gloves pick up blood from the tool they hold, fingertips first, and the sink or a fresh pair cleans them. Bloody
  gloves slowly stain the scrubs, which stay stained for the rest of the surgery.
- Patient vitals are only on the bedside monitor, never on the player's screen. The monitor is laid out like a real one:
  sweeping ECG, pleth and breathing traces, numbers in their trace's color, alarms in the top bar and lab results along
  the bottom.

### Lighting captures and benchmarks

Capture paired Forward+ room, patient and station views (also accepts `ambulance_bullet` and `sidewalk_stab`):

```bash
RENDERER=forward_plus SHOTS_ARGS='--lighting' ./build.py shots appendectomy build/shots
```

For GPU timings, use the Godot executable provisioned by `./build.py setup` on the native display, with the bundled .NET
runtime on `PATH` and in `DOTNET_ROOT` when needed:

```bash
GODOT --path . --rendering-method forward_plus --audio-driver Dummy --resolution 1920x1080 \
  res://tests/Support/Screenshot.tscn -- \
  --scenario=appendectomy --out=build/shots --lighting --benchmark
```

The benchmark freezes the simulation and shader clock, disables film grain, V-sync and the frame cap, then runs
off/on/on/off passes per view: 180 warm-up frames and 240 GPU-timestamp samples each.
`performance.csv` records the adapter, resolution and GPU/frame statistics.
These steady-state captures exclude GI startup; `./build.py performance_test --render-budget` checks moving gameplay
against the frame budget.

## Core mechanics

- Two-hand control, one active at a time, idle hand frozen mid-action (see Controls).
- Holding tissue anchors the hand; walking away tears it.
- Hand bumps between surgeons. Jolts from seizures, coughs, potholes, pedestrians.
- Cuts with depth and speed (clean vs jagged) through skin, fat and muscle, in the soft tissue sim (see Look).
- A blade pressed in without moving goes in as wide as itself, at its depth level. Moved along its edge it cuts on, also
  past the end of an opening it's already in; inside the opening there's no wall left to cut. Drawn along a cut already
  there, it deepens that cut rather than making a second one, and opens it again where it goes: its stitches are cut, a
  clot or a cautery seal on that stretch no longer holds. Over an opening it reaches down only at full depth: there it
  grates on a bone (which hurts through a local block) but stops short of an organ; it nicks an organ only by touching
  it. Drawn on from cutting the belly wall or slid over from elsewhere in the opening, it passes over a target; put down
  on one, it cuts it free (an appendix's base then bleeds from its vessel).
- Per-segment closure: sew along the whole wound. Weak closures burst under strain.
- Bleeding per wound, blood pooling on skin and in the cavity, suction, gauze pressure, clamps, cautery, tourniquet. A
  surgeon's clean incision only oozes (`Wound.CleanIncisionShare` of what a jagged cut or a tear bleeds), so opening a
  belly properly is no emergency; a stretch cut too fast or with a dull blade bleeds in full. Gauze pressed on a wound
  stops it for a while after it comes off (`Wound.PressureHold`), then the bleeding comes back; it stops a cut too small
  to sew (`Wound.SmallCut`) for good when held on it long enough without a break. Packed into an opening, it presses
  everything it lies against at once: each vessel it reaches at the bottom and the stretch of the cut's edges under its
  pad. The `stop_bleeding` objective counts only what stays stopped without gauze pressure. Debug mode toasts when a
  wound stops bleeding, `[permanently]` or `[temporarily]` (held only by gauze).
- Drugs with onset/duration curves, direct vs IV routes, allergies, dangerous combinations, blood type matching.
- Cardiac arrest: V-fib, asystole, shocks, adrenaline windows, zapping a partner who's touching the patient. Arrests
  follow from the patient's state (blood loss, low pressure, racing pulse, fever, sugar out of range, swelling, clots,
  heart quirks); there is no random arrest event. Scripted arrests (heart attack) always happen.
- General anesthesia holds for the whole surgery once given, side effects included (propofol and gas keep the pressure
  about 15-20 mmHg down, the manual says how to manage it). A repeat dose only tops it up. It wears off only with the
  anesthesia resistant quirk, a scripted wake up (awake craniotomy) or once flumazenil works (in this game it also
  reverses general anesthesia): a right dose then wears off in `Patient.WakeSeconds`, and a new dose puts the patient
  under again.
- Random events only disturb the surgeons (a pothole, a cough, a bump, flickering lights with the power trouble
  modifier). Nothing random happens to the patient that the chart, the monitor and the manual don't explain.
- Seizures, malignant hyperthermia, diabetes drift, panicking awake patients.
- Organs you push or hold aside, targets you free by cutting, sawing, slow pulling or suction. Deep cuts reach bone.
- Dropped tools: floor makes them dirty, dropping into the cavity cuts something, heavy tools break fragile bones.
- Sterility tracking into the post-op report (infection, amputation).
- Nurse orders (see Starter kit and ordering), blood panels with narrow/fast vs full/slow choices.
- Turning the patient as a shared quick time event, all surgeons on one side.
- Personal gauges: stress (pass out), sickness (vomit), breath (steady hands), sweat (slippery gloves, drips).
- Belt inventory, personal quirk items, drinking and wearing items, smoke breaks at the smoking spot.
- Manual with Divine knowledge highlights, patient card with red herrings.
- Scoring, stars, delayed consequences, codex unlocks, 24 scenarios.

## Guidance, hands and room

- **Figure it out**: at the MD difficulty, no objectives, score or "done" toasts on screen, and the chart shows only the
  complaint (symptoms and history), never a plan. The manual is the one reference. Everything is still tracked
  internally; the easier difficulties show the objectives (see [Scenarios](DATA.md#scenarios-datascenariosnn_idcfg)),
  and the debug setting the score and every scored action. Easy scenarios (1-2 stars) have no time limit.
- **Hands**: no height control. A hand rests the tool tip just above whatever is under it (skin, tray, organs or a
  target in an open cavity). The hand and the end of the forearm also keep clear of what's under them, so nothing sinks
  into a leg. Hands of two surgeons in the same space collide. Hands stay within reach and hang at waist height when
  nothing reachable is below. Crouch reaches the floor and walks slowly. Zoom toggles between two levels (hand motion
  scales with the magnification for precision, so the hand crosses the screen as fast at both); the closer one makes the
  hands see-through. Zoomed in with the hand over the patient (skin, drapes or an opening, holding a tool or not,
  working or not), the surgeon leans in: the torso bends forward from the hips (everyone sees it) and the eyes come over
  the hand and look down at it, into an opening past its edges, never turning sideways, so the mouse still moves the
  hand left and right on screen. Reach and collisions don't change. The hand off the patient for a second, zooming out
  or crouching straightens back up; a needle view keeps the bend under it. Walking away also zooms back out. The hands
  start apart, turned in toward the middle, so each tool points across in front of the eyes, beside its hand. The elbows
  bend out to the sides, so the player's own arms come into the view from its bottom corners, not up the middle in front
  of the work. The player's own arms are drawn only from the rounded elbows down; partners see whole arms. The tool the
  empty hand would pick up is highlighted and named at the aim dot; Grab takes it in one press.
- **Grips**: every tool has a grip (`grip` in tools.cfg: pencil, rings, fist, flat) that places the glove on it and
  curls each finger. The glove then turns around the tool toward the forearm, only as far as a forearm turns, so the
  back of the hand stays up. `data/grips.json` fits each tool model to the glove (moves it off the tool, opens or closes
  fingers) so no tool goes through the hand; `tests/Support/FitGrips.tscn` makes it.
- **Tools on hard surfaces**: tools lie on the tray side by side at the start, a lowered tool only presses into skin,
  and every corner of a held tool and the glove clear tables, trays and tools lying there. Physics is Jolt.
  `tests/Support/GripGallery.tscn` renders every tool held, for checking.
- **Stations**: the nurse menu is a shop: categories (`category` in tools.cfg), each a list of items with [-] count [+],
  and a cart. Deliveries land side by side on a delivery tray. The defibrillator always waits on its own cart. Station
  cabinets are solid.
- **Patient card**: a hospital record hanging on the rail across the table's head end, in view from both starting spots.
  Every surgery opens with each player reading their own copy; put back, each copy travels in an arc to the hook and
  they merge into one. Taken, the card travels up to the reader's face before the page shows, leaves the hook for
  everyone and is seen in the reader's hands until put back. Purely visual: no collision.
- **Discharge**: the surgery ends when a player signs the patient out at the foot of the card's page: holding Interact
  for `SignatureView.SignSeconds` writes the signature under a see-through pen, letting go early unwrites it. The host
  then settles the outcome, score and consequences. Signed out too early, the patient can still go, as a failure. Only
  death and the time limit end a surgery without it. Leaving the table (signed out or out of time) also scores
  aftercare: a patient who was put under and is awake again, the IV taken out and every hole it left taped, and skin
  with at most `Surgery.SkinBloodLeft` of the most blood it held left on it (the site without its border of
  `PatientBody.CleanMargin`).
- **Post-op report**: the score lines, then a timeline of the surgery: each scored action, each step first done or
  spoiled (naming the step it comes after that wasn't in place), then at the discharge which steps counted and which
  were missed. It's where a player learns what the hidden objectives were. Repeats in a row merge into one line; past
  `Scoring.TimelineSize` lines only the discharge's go on. Every peer keeps its last 100 games locally; the main menu's
  History lists them (scenario, outcome, stars, score) and opens the same report.
- **Floor dirt**: a tool that hits the floor is soiled and unsterile. Wash it at the sink, then sanitize it.
- **IV line**: loose, flexible tubing from the drip chamber on the stand, its bare end hung on the stand across from it.
  It hangs under gravity and is pushed aside by the floor, furniture, the patient's skin, the drape and surgeons. Every
  peer simulates its own. Holding a catheter, "Connect the IV line" at the stand puts the tubing's end on it; pressed
  onto an arm, the catheter goes in and the tubing hangs from it. It has to go into the forearm vein with the tubing on
  it to work: beside the vein, or without the tubing, it still sticks, but nothing goes through, and the catheter can't
  be connected afterwards. The last zoom step fades the hands as for a syringe. Where it went in, the catheter is taped
  down on the forearm: its stub going into the skin toward the elbow, the hub with a colored cap and wings, a clear film
  over it, two strips of woven tape across the arm and the tubing taped along the arm before it hangs off to the stand
  (one strip, no tubing, without it). A surgeon who catches the tubing on their legs or hands and walks off stretches it
  past its length (`IvLine.YankStretch`): held stretched for a moment, the catheter rips out of the arm, leaving a
  ragged tear in the skin and falling, still on the tubing's end, as a catheter to pick up and put back in (the floor
  soils it like any tool). Pushing into the slack only moves it aside. Connecting a new catheter replaces the one on the
  end; one carried further from the stand than the tubing reaches slips off. Holding the catheter the tubing is on,
  "Disconnect the IV line" at the stand takes the tubing off and hangs it back on the stand: a catheter that fell is
  washed and sanitized bare, then connected again and put in. An unsterile catheter put in scores as a dirty tool, as
  one on the site does, without soiling the site. Interact on the dressing ("Remove the IV") takes the catheter out for
  good: the tubing goes back on the stand and a small hole is left where it went in, oozing like a tear until taped.
- **IV drip** (`iv_drip` in tools.cfg): the bag on the stand is a fixed tool, 500 ml of fluid with room for 100 more. A
  syringe brought over the bag snaps its needle into the bag's middle, straight into the face on the hand's side and a
  little upward, the way the forearm rises to it; moved on, it comes out and the hand holds the syringe as before. While
  it's in: push a drug in and it starts down the line at once, ahead of the bag's own fluid, at 2 ml a second, if the
  line is in a vein. Each bit is given as it reaches the patient; debug mode tells each ml and the total so far. Pull
  and the syringe draws by the port: what was pushed in and hasn't run yet first, then the bag's fluid. Holding a saline
  or blood bag, the stand offers "Swap IV bag": the held bag replaces the hung one and runs in as a full dose.

## Controls

- **Look by default**: the mouse looks around like a regular first person game. Holding a hand's key (Q left, E right)
  moves that hand instead and makes it the active one. Hands turn and walk with the body unless they hold onto something
  (a gripped clamp or retractor), then they stay put.
- **One button per job**: RMB picks up and puts down. Holding LMB uses the active tool: it lowers onto its spot and
  presses its single action, so clamps pinch and let go, the mallet strikes, the tourniquet goes on, a graft goes on,
  the defibrillator charges while held and shocks on release. A clamp's jaws close once its tip has come to rest on
  what's under it, so one pressed while still coming down (out of an aim, or a remote player's late moves) takes what it
  lands on. Forceps holding a cotton pad wipe or dip it, and let it go when used in the air away from the dish. Forceps
  holding a graft taken from the skin put it on a cleaned burn, or let it go in the air. The Rotate keys (C/V) roll a
  held tool about its length: a scalpel's blade turns with it, to follow a curve. A hand that lets go of a rolled tool
  rolls back to palm down; an empty hand has nothing to roll. Holding MMB the mouse turns the held tool about the wrist
  (tilt up and down, turn left and right): the wrist and forearm stay put, the tip follows the mouse and rises off what
  it rested on, and settles back down once MMB is let go. Tools with a range take an effort level 0-3 from the wheel
  (cut depth, heat, saw speed, suction), 0 does nothing. Gauze and other swabs take none: lowered, they wipe right where
  they point, and gauze presses every cut under its pad, a little past its edge, held in a hand or taped down. Shift
  toggles between two zoom levels. A syringe has its own wheel instead: down pulls the plunger, up pushes it (see Vials
  and syringes). So has the needle: down tightens its thread, up loosens it; a click stitches and a hold ties off (see
  the running suture), and paper clips, pulled the same way. And the Gelpi retractor: up opens it, down closes it (see
  Gelpi retractor). Tape and the other closures take no wheel: tape is clicked down like the needle's holes (see Tape),
  the rest close where they're pressed on. And forceps and hemostats over an opening: up raises the tip a notch
  (`Surgeon.DepthStep`), down lowers it, from the lip down to resting on what's inside, never into it. Held at a depth,
  the tip stays there as it moves (riding up over organs) and when Use tool is pressed, so the jaws close where the tip
  is: on a target, vessel or bleeding wound only with the tip down on what's inside (`Patient.JawReach`), on the cut's
  edge only up by the lip. Deeper than `Patient.LipDepth` it can't move out under the skin, anywhere along a move: it
  slides along the lip, and raised to the lip it goes over. All the way down, or out of the opening, it rests on what's
  inside again. The aim shows a stem from the lip down to the tip and the depth beside it. Holding onto something, the
  wheel moves the hand instead: up pulls it a notch higher, down lets it a notch lower, never under what's inside. Notch
  by notch, something still attached comes loose; scrolled fast, it rips like any fast pull. A target pulled out is
  carried, no longer holding the hand to the patient: it walks with the surgeon, the tip kept as high over the lip as it
  came out. The retractor (a skin hook) and forceps holding a pad or graft take no depth.
- **Contextual aim**: shown on whatever is right under the tool's tip, where Use tool brings it down, so it's accurate
  at any angle. A dot for point tools, a line along a blade's edge for blades, a < and a > at a Gelpi retractor's tips,
  a ring under each of a stapler's legs. Every mark lies on the surface under the point it marks, not in the air at the
  hovering tool. Aimed across a cut, a Gelpi's < and > hang down from the lip on each side into the wound, where its
  tips go in. A stapler's legs go in exactly where its rings were shown when Use tool was pressed. A needle's hole goes
  in right under its dot, and rides along with the tissue on its side of the cut. A suture needle rests on the tissue as
  it's drawn: moved out over a cut from its lip it goes down the wall beside it, through the skin's, the fat's and the
  muscle's cut faces, then into the cavity, and its dot is on what it touches. The hole goes into that layer. An edge
  drawn back more than a few millimeters under the layer over it can't be reached until the cut is spread or its lip
  lifted. A stab or bullet hole shows no muscle, and skin won't close over the muscle open under it: it's cut open to
  sew the muscle first. The edge is where the blade plane meets the skin, so rolling the tool (C/V) or turning it (MMB)
  turns it. A blade only cuts moving along its edge; sideways it drags.
- **Controls shown for what you're doing**: the bottom right hint changes while a hand key is held or a tool is lowered.

## Starter kit and ordering

- **Same tray every time** (`data/starter_kit.cfg`): scalpel, forceps, kidney dish, cotton pads, iodine bottle with its
  dish, a 3, 10 and 50 ml syringe, IV catheter and saline bag. Everything else is ordered.
- **Ambulance cabin** (`Room.Ambulance.cs`, `props.py`): fitted blue and white storage, rear double doors, a cab
  partition, folded attendant seat and ceiling grab rails. References: the [Ambulance Patient Compartment Human Factors
  Design
  Guidebook](https://www.naemt.org/docs/default-source/ems-health-and-safety-documents/health-safety-grid/ambulance-patient-compartment-human-factors-design-guidebook.pdf?sfvrsn=2)
  and [NAFFCO ambulance interior catalogue](https://www.naffco.com/media-downloads/pdf/ambulance.pdf). The current
  monitor model and module rack mount to the side wall beside the patient's head, using a swivel bracket instead of the
  bed clamp. The IV pole sits beside it, clear of the display and overhead cupboards. The stretcher divides two
  one-person aisles; end lockers prevent walking round it. Surgical instruments, closures and dressings occupy the +Z
  compartments; IV supplies, medication and suction occupy -Z. The briefing warns that co-op is intended, while leaving
  solo selectable. The fixed kit cannot randomly lose essential tools, since there is no nurse to replace them.
- **Tray layout** (`tray` in tools.cfg, `Room.TrayZones`): scalpel and forceps lie in a small steel tray, the cotton
  pads in a pile in another, the syringes side by side and the bottles and vials standing at one end. The rest fills the
  space left.
- **Nurse**: one order at a time, a cart of up to five items (the same one twice too) fetched together, so it takes as
  long as its slowest item. A 15 s cooldown after each delivery from the sixth on (the first five come without). Every
  drug is under one Drugs group. A board over the bell shows the batch on its way with a progress bar, then the
  cooldown.
- **Skin prep**: pour iodine from the bottle into a dish (20 ml a second while Use tool is held), pinch a cotton pad
  with forceps (or a hemostat), dip it, wipe the skin. A pad held in the glove or picked up off the floor contaminates
  the site. A pad soaks up 10 ml (its `volume`), so the 40 ml iodine dish soaks four; a soaked pad runs dry after about
  8 s of wiping.
- **Swab capacity**: gauze (20 ml) and cotton pads (10 ml) hold what they soak up in their `volume`, iodine and blood
  alike: a pad half full of iodine has room for half as much blood. Wiping takes blood off the skin, out of cuts and the
  cavity (`Patient.SwabRate`) only while there's room for it; a full swab no longer wipes, but gauze still presses on a
  wound. The whole swab takes the color of what it holds, iodine brown and blood red mixed by their shares, deeper the
  fuller it is. Blood soaked up bloodies the glove holding it.
- **Dishes** (a `volume` and no action of its own: the iodine dish and the kidney dish) all work the same: bottles pour
  into them, syringes squirt into them and draw from them, pads dip into iodine in them. They show their liquid by its
  ml, tinted toward iodine and blood by their share.

## Vials and syringes

- **Drugs come in labelled vials** (`vial_*` in tools.cfg): the name is the label, with the strength per ml. Syringes
  (3, 10 and 50 ml) say only their size and whether they're full or empty, so players keep track of what's in which. The
  barrel is glass with a scale printed on one side, like a real syringe's: fine ticks (0.1 ml on the 3 ml, 0.2 on the
  10, 1 on the 50) and numbered ml. The face of the black rubber stopper reads against it.
- **Reading a vial with a syringe**: a held syringe whose needle is in or just over a vial shows the vial's label beside
  the aim, as a tool's name shows there for an empty hand.
- **Snapping to a vial**: a syringe whose tip passes over a vial's cap snaps its needle in through the cap along the
  vial (down into one standing, level into one lying), before Use tool is pressed. Only a cap that faces the surgeon
  snaps: up, or lying, pointing their way. Vials and bottles come from the nurse standing, cap up.
- **Drawing from a vial in the other hand**: a syringe brought within 8 cm across the floor (and 15 cm up or down) of
  the fist holding a vial in the other hand meets it halfway: the vial turns cap up and both hands ease to a point
  between the needle and the cap, at the higher of the two, the needle going down into it. A vial lying about under the
  needle comes first, and a needle pressed into something else or stuck in the patient doesn't meet it. Moved away, the
  vial hand goes back to where and how it held it.
- **Standing a bottle up**: holding Grab a second with a bottle in hand stands it upright where it's held, on whatever
  is under it. A click puts it down like any other tool. It lets go a little further out than it snaps in, so passing
  over doesn't hold it for long. Snapping in and out (onto the IV bag too) eases over 0.25 s, the needle gliding over
  rather than jumping.
- **Plunger on the wheel**: wheel down pulls the plunger out 1 ml a notch, wheel up pushes it in 1 ml, whether or not
  Use tool is held. The needle is in whatever its tip rests on or just over: over a vial or the dish it rests there, on
  the patient Use tool presses it in. A syringe holds ml plus an amount of each drug, so drawing from a second vial
  mixes. Air sits at the needle end and goes out first. Pushing into the patient or a surgeon gives what's pushed as it
  goes in, one notch at a time.
  - a vial, a dish (the kidney dish holds 100 ml, the iodine dish 40) or the IV drip: pulls its liquid, pushes into it.
    A full vial or bag takes no more.
  - a vein drawn on each forearm (not on an arm the site covers): pulls blood, which tints the liquid toward red by its
    share, pushes the drug in as an IV dose without a line.
  - skin, fat or muscle (the deepest layer a cut opens there): pushes a direct injection, pulls nothing and the plunger
    stays.
  - a surgeon's glove (the other hand of the one holding it, or a partner's) or a partner's body: the needle rests on
    the back of a glove, wrist to fingertips, like on skin. Pushes a dose into that surgeon, pulls nothing. A glove
    comes before the patient under it, a body after.
  - nothing: pulls air, pushes the liquid out in a squirt.
- **Needle in the patient sticks**: with Use tool held and the needle in a vein or tissue, its tip stays exactly where
  it went in (no tremor, no lift). Moving the mouse toward or away from the body tilts the syringe about the tip, the
  hand swinging round it. What the tilt can't follow (sideways, or past the tilt range) stretches the skin by a fifth of
  the motion; stretched 1.5 cm, or walked away from out of reach, the needle tears out: a short scratch, a bead of blood
  and pain. It then moves freely until Use tool is let go. Releasing Use tool withdraws it immediately and leaves a
  visual blood bead exactly at the puncture, without adding pain or a scratch. Moving the hand afterward cannot tear it
  out.
- **Held facing you**: picked up, a syringe is held ready to inject (grip `syringe`): the index and middle fingers over
  its finger grip, the thumb on the plunger, following it in and out. It points a little down and in toward the body's
  middle, the hand off to its outer side, with the printed scale turned toward the eyes, so it doesn't need turning to
  be read. Let go, the hand holds things the way it did before. Moved about, it keeps turning the scale to the eyes, so
  C/V don't roll it (and the controls shown leave them out).
- **Needle view**: the last zoom step fades the hands to see through whatever they hold, so the needle and where it goes
  show. The camera stays at the eyes, so aiming moves the hand the way it always does. Use tool with a syringe zooms all
  the way in on its own and back out when let go. Once a syringe's needle is in (Use tool held in a vial, the dish, the
  bag, the patient or a glove), the camera moves beside it, side on and a little above, so the needle and what it's in
  are in view, and the hand rolls the syringe about its length so the printed scale faces the camera. There the mouse
  moves the hand as seen from the camera: right on screen is right, up is away from it. Use tool let go, the camera goes
  back and the scale turns back to the eyes.
- **Dosing**: the chart shows the patient's weight, the manual the dose per kg (`dose` in drugs.cfg). Doses add up, for
  the patient and the surgeons alike: every injection goes into a depot that soaks into the blood over the route's onset
  (a direct injection 0.4x the drug's onset, a vein or the IV line 1.5x), and the level in the blood drops by one right
  dose every `duration` seconds, so ten 1 ml shots work like one 10 ml shot and twice the dose lasts twice as long.
  Between 0.7x and 1.4x the right dose in the blood works as the right dose; below or above it scales. Under half a dose
  it has only a faint effect and doesn't do its job (no objective, restart, antibiotic...); reaching half a dose it
  does. 2.5x taken in (in the blood or still soaking in) is an overdose; bags of fluid or blood have no dose to overdo.
  General anesthesia holds at the right dose (topped up by the anesthetist): more wears off as usual, and a patient who
  burns through it loses it all. A lethal drug that worked ends it some time later, however fast it wears off.
- **Weight**: rolled per age group, heavier with a heavy build quirk. The body model scales with the cube root of it.
  Surgeons weigh 80 kg (small hands 60), shown in the lobby under their name; doses given to them use it.
- **Breaking**: a syringe that hits the floor shatters (`fragile` in tools.cfg).
- **Reading it**: holding Inspect (X) brings the tool in the active hand up in front of the eyes, across the view with
  its tick marks toward them. Liquid, air and plunger follow the ml exactly on every peer.

## Stress, tremor and sedation (`SurgeonStatus`)

- **Stress is the tremor**: under 30% the tool stays still and only the glove twitches now and then; up to 60% a light
  shake reaches the tool; above it a plain one that grows with stress. Quirks only change how fast stress builds
  (`stress_mult`) and how low it drains (`stress_floor`): Shaky hands 65%, Alcoholic 35% (none while a sip works), a
  coffee +15% while it works. Cold (run modifier) shakes on its own. Holding breath steadies all of it, Steady hands
  takes it all away.
- **Sedatives for a surgeon** (diazepam, morphine, propofol, ketamine, pentobarbital: any drug with `sedation` or
  `anesthesia` but an antihistamine): injected into a hand (their own other hand or a partner's) or a partner's body,
  dosed by the surgeon's weight. Each works like diazepam (morphine but for the hands, below), its share of its own
  right dose adding up with the others. At the right dose it stops stress shaking (not the cold) while it works, blurs
  the view and delays the mouse's hand moves by 100 ms (looking around isn't); afterimages trail the gloves. Past 1.4
  times the dose the view darkens toward the edges and the delay grows, up to 300 ms. Twice the dose knocks the surgeon
  out for five minutes: they tip over sideways onto the side with more floor, lie facing the table with the view 80%
  dark at the edges, can't do anything and moan now and then (`surgeon_moan`, heard by everyone). Flumazenil
  (`reverse_benzo`) ends diazepam and naloxone (`reverse_opioid`) morphine, bringing them round unless the other
  sedatives still knock them out; propofol, ketamine and pentobarbital have no antidote. Adrenaline (`stimulant`) gets
  them up only while it lasts, and if enough sedative is still working they go down again. On the patient, flumazenil
  reverses diazepam too.
- **Morphine nods a surgeon off** (`opioid`): its share of the sedatives doesn't delay the hands or trail the gloves.
  From half the dose, every 10 to 30 seconds the eyelids close from the top and bottom over 0.3 s (corners first), stay
  shut 1 to 2 s and open over 0.4 s.
- **Stimulants for a surgeon** (`stimulant`: adrenaline injected, cocaine snorted from its baggie with the drink key):
  hands 1.3 times as fast, stress building 1.5 times as fast, and a sub-bass heartbeat (`heartbeat`, heard only by them)
  at 110 a minute while it works. Two drugs `danger_with` each other at work at once (adrenaline and cocaine) race the
  heart: hands and feet 1.6 times as fast for three minutes, the heartbeat speeding up from 140 to 200. Nothing stops
  it. Then the heart stops: the surgeon drops what they hold and goes down like a knocked out one, the heartbeat still
  pounding at 200, and only they see a 30 s countdown in the middle of the view. A partner's defibrillator lowered onto
  their chest (a hand comes down onto a surgeon lying on the floor, not through them) and let go charged restarts it
  half the time (the host rolls each shock). Brought back, they get up, and the heartbeat and the hands and feet slow
  down to normal over a minute (from 200 to 70 a minute, then quiet). Not brought back in time, they die (down for good,
  the view black). Once every surgeon is dead, the operation fails.
- **A surgeon collapsing** for any reason (passing out, knocked out, a stopped heart) lands with a thunk
  (`surgeon_collapse`) where they fall, heard by everyone.
- **Lidocaine into a glove** (`local_block`, any amount) numbs that hand for a minute: it drops what it holds, also a
  tool handed to it later, and its grab doesn't close.
- **Diphenhydramine** (`antihistamine`) isn't a sedative on a surgeon: it takes nausea away and keeps it away while it
  works. **An antidote** (`reverse_benzo`, `reverse_opioid`) with nothing of its kind to reverse adds 0.4 stress.

## Gelpi retractor

- A self-retaining spreader (`action="spread"`), beside the plain retractor that pulls one edge like forceps: ring
  handles with a ratchet rising to a box joint and two long arms, each ending in a point bent down under it. It's held
  tipped toward the skin, its points down, and lies along a cut with its jaws across it. The aim shows a < and a > where
  its tips are, square to its length; C/V swing it about the upright to turn them across a cut. The wheel opens and
  closes it, in the hand or set, from 1.2 to 8 cm between the tips; the arms swing about the joint to match, the handles
  stay in the fingers.
- Use tool on a cut sets it: each jaw takes hold of the skin on its own side of the middle, so set right over a cut each
  holds one edge. It goes down lying flatter along the skin, its points into the cut as deep as the cut goes (through
  the skin, the fat or the muscle, at most `ToolActions.SpreadReach`), so how far the arms sink shows how deep the cut
  is. Pressed on skin with no cut between its tips it doesn't set: the hand bounces off and comes back down. Opened or
  closed, each edge moves half the change, and the cut opens like the skin lets it. Once the skin either jaw holds stops
  giving (`ToolActions.SpreadStrain`) the wheel opens it no further: past that the skin would only bunch into a ridge at
  the drape. Use tool again takes it out.
- Set, it stays where it went in: the hand holding it goes to it and doesn't shake. Put down, or walked away from, it
  stays set in the wound (self-retaining). Picked up again, the wheel works on it. Left set, it doesn't keep hands or
  tools off the cut: a blade cuts the layers under the skin between its jaws.

## Retractor

- Use tool hooks the skin where it's pressed, and moving the hand pulls it that way, like forceps. Grab lets go of the
  handle but not the skin (self-retaining): the retractor lies down along the body from the hook, pointing away from
  where it took hold, and keeps the skin pulled where the hand left it. It lies flat on the body rather than standing
  up, and slides under the drape's edge. It rides the site, rising and falling with a breathing belly, and so does the
  skin it holds. Its handle points straight away from where it hooked: the way it pulled, or away from the cut. Taken
  back, the hand goes to where the hook holds, so the skin isn't dragged; Use tool unhooks it.
- It hooks only skin, never a target, vessel or organ under it: the skin it's pressed onto, or pressed into an opening,
  the edge of the cut on that side. It pulls the skin aside or up, never down into the opening after its tip.
- Anything left holding onto the patient (a retractor, a hemostat, a Gelpi retractor) has no collider: hands and tools
  reach past it.

## Staplers

- The skin and office staplers (`action="staple"`) put in one staple per Use tool press, no wheel. The aim shows two
  rings where the legs go in, a fixed `staple_span` apart across the tool; C/V turn them across a cut. The skin
  stapler's legs reach out up to `staple_give` for the edge of an opening, the office stapler's don't. The rings lie
  where the legs land, tools in the way left out: on the skin, or down in the opening when a leg can't reach an edge,
  and then the press does nothing. A press staples only with both legs on skin either side of a cut not closed there;
  otherwise the one who pressed is told why.
- A staple joins the skin edges within `TissueSim.StitchReach` of where it crosses the cut; the cut counts closed where
  its edges then meet, like any closure. Where the muscle under it is still open it goes into the muscle instead, so a
  cut through the muscle is stapled twice along.
- Staples are drawn as steel wire bridging the cut, each leg riding the tissue where it went in.
- The office stapler has a `tear_chance` (the staple tears out, a new tear) and a `bleed_chance` (it goes through a
  vessel: the stapled cut bleeds through it, until cautery seals it or a clamp or pressure holds it) per staple. A nick
  is no new wound to close.
- How closed the skin is is weighed by each wound's length: a small hole left open counts for as little of it as it is.
  The "close" objective is met at `Patient.ClosedEnough` (0.9) in every scenario.

## Tape

- Surgical and duct tape (`action="tape"`) are worked like the needle, no wheel: a click on the site presses a strip
  down there, the first starting it off the roll, and each later click sticks it down all the way from the one before. A
  hold presses the last point down and cuts the strip off the roll; one pressed down at a single point comes away with
  it. Each point takes a charge.
- A stretch stuck down holds every cut edge under it (its `radius` either side of its line) at the tension skin closes
  at, so the cut counts closed where they then meet, like any closure. Not over cut muscle: that holds the skin apart,
  and tape can't reach it.
- How firmly it sticks is its grip: the tape's `power` times `Patient.TapeGrip`, less on blood on the skin under it
  (`Patient.WetGrip`) and on a cut into the fat (`Patient.DeepGrip`). It pulls together only edges lying no further
  apart than that: on a deep or gaping cut, such as the forearm laceration of hand stitch, it peels straight off. Once
  they're shut it lets go when they're pulled apart by a share of its grip (`Patient.TapeHold`): a hand pulling the skin
  beside it, which a strip on blood doesn't survive. Blood oozing from the cut under it soaks in and loosens it further
  (`Patient.TapeSoak`). Let go, it peels off, the cut opens again and it costs points.
- Gauze it goes over, lying on the skin or held over it in a hand (either surgeon's), is taped down there: it lies flat
  on the body, riding the site, and keeps pressing the cut under it as gauze pressed by hand does for as long as the
  strip holds. A stretch over gauze holds the gauze, not the cut's edges under it. Held, the gauze stays in the hand,
  which stays on it, flat (the `press` grip), and keeps the surgeon there, until it lets go (Grab): then the gauze stays
  where it was taped. Taken off, it takes the strip with it.
- Taped gauze soaks up what its pressure holds back from the cut under it until it's full; then the blood soaks through
  into the tape (`Patient.SoakThrough` per ml), and soaked through, the tape lets go and the gauze with it, pressing no
  more.
- The hole an IV catheter left, or the skin it tore ripped out, oozes (`Patient.IvTearOoze`) until a strip across it
  holds it shut; each one stays until taped. On the forearm a strip is short: a press near the tear starts it, the next
  sticks it down from there and cuts it off. It holds when its middle lies over the slit and it reaches past the torn
  rim on both sides, and is then drawn across the arm there, the blood bead under it wiped; otherwise it doesn't stick
  and takes no tape.
- A strip is drawn flat along the skin between its points, over gauze it holds down, and from its last point to the roll
  while it's still on it. A strip that came off is gone.

## Tourniquet

- Pressed onto an arm or a leg it wraps around the limb there: a band snug on the skin, and the hand lets go of it.
  Grabbing it again takes it off.

## Holding the patient

- Use tool with an empty hand aimed at the patient lays the hand flat on the skin there and holds it (the `press` grip):
  palm and fingers on the skin, pressed a little into it. A limb it grasps: a palm bends only at the wrist and the
  knuckles, so it lies on the limb stiff, giving only a little under pressure. It is turned round the limb, touches it
  where along its length lays it closest, and leans along it. On a limb about as wide as the palm is long it lies across
  the round like a chord, heel and knuckles lifting off. Held firmly, it presses into its palm, slanted toward the near
  side (on the limb's top: down and toward the surgeon), until the palm gives as much as a flat hold's, the fingers
  closer round the far side. Skin facing along the limb (the top of a foot) is held flat instead.
- The fingers across the limb wrap its far side (over a cut there) from the knuckle out: each joint bends until its bone
  rests on the skin. Over an opening on the trunk the hand rests on the lip without pressing in, its fingers straight
  across it, so none dips in.
- Where the drape covers the body the hand holds the skin under it, pressing the sheet down.
- Use tool again or Grab lets go; so does walking out of reach.
- It stays on the spot it holds as that part moves, and keeps that part still: an arm, a leg or the head stops flailing.
  The trunk still breathes.
- Like gauze it holds the cuts in the skin under the palm, the one an opening is in too, without soaking anything up or
  reaching what bleeds inside.

## More mechanics

- **Pass the tool**: press Grab with a tool near your partner's empty hand to hand it over. Moving hands fumble it onto
  the floor.
- **Organ handling damage**: organs held out of place for more than a few seconds, or shoved hard, bruise (they darken)
  and ooze.
- **Suture tension**: the wheel sets the needle's thread tension and paper clips' pull alike, down tighter and up
  looser, and the hand status reads it (loose, closed, too tight). Loose leaks, too tight can tear through. The needle
  pulls its current thread only: without one the wheel does nothing, and each new thread starts loose.
- **Run modifiers** (`data/run_modifiers.cfg`): 1-2 per run, rolled in the lobby so both players see them before
  starting. A column beside the surgeons' cards explains this run's; the main menu's Run modifiers lists all of them,
  the locked ones hidden. A modifier unlocks the first time a run starts with it; locked ones roll all the same.
  Understaffed, expired drugs, bad wiring, med student, short supplies, blood shortage, broken heating, chart mix-up.
- **Chart mix-up** (a run modifier): the patient card shows a wrong blood type and allergy and misses a real condition,
  until the nurse brings the corrected copy partway through.
- **Blood pressure**: panic and stimulants (adrenaline, cocaine, ketamine) raise systolic pressure. Above 140 mmHg, and
  while heparin acts, sutures, staples, tape and gauze leak; cautery and clamps hold. Patients with fragile vessels (an
  aneurysm) can burst a deep vessel under the site above the same pressure.
- **X-ray cart**: it starts put away in a niche in the wall left of the table's foot, out of the way round the table.
  Taken hold of like a tool, anywhere on it (Grab), it's pushed to the table, the wheel slides the level arm up and down
  the upright column (it starts with the lens just over the surgical site) and Use tool takes an exposure. Let go, it
  locks against the floor. It rolls right up to the walls and back into its niche. What the player can take hold of or
  interact with lights up while they look at it. The collimator lights two fields on the patient: AP, straight down from
  the tube head, and lateral, level and forward from just below it. An exposure takes both films of the scene as it is,
  the patient's skeleton included, which only films see. Bone and metal come out white, soft tissue and organs as faint
  outlines, a bullet's wound track as a grey streak. The films develop in the cassette holder over six seconds. They're
  black where the beam got through and clear where something stopped it, so a player holds them up against a light to
  read them.

## Ideas not yet approved

- Scrub nurse mode, anesthesia machine station, vital-sign callouts, patient's family calling mid-surgery.

## Assets and animation

Most models and all bundled sound effects are generated from code (`./build.py assets`).
Supplied models and source assets are identified in [the model catalog](../assets/models/README.md); preserve those
inputs when regenerating.

- **Organic and rigged models** (`tools/blender`, Blender as a Python module): the patient, the surgeon's glove, organs
  and anatomical targets. The patient is one skinned mesh; eyes, lids, hair, brows, teeth and tongue ride their bones.
- **Hard-surface models** (`tools/assetgen`, trimesh): props and instruments, exported to `.glb` with named parts.
  Hand-made pieces some models are built from (the glove box, the IV bag) live in `tools/assetgen/sources/` and take
  palette materials.
- The game swaps materials for the cel shader by name (`src/Visuals/ModelSlot.cs`).
- **Patient skin**: cuts, burns, bruises, blood and iodine sit on the body model itself. Only around cuts and skin a
  tool holds is the model replaced by the simulated skin, fat and muscle layers, which hug the body as it is, whatever
  the model becomes. Every peer measures the same model the same way, so every player sees the same site. The cavity
  under them rises to just under the skin at the site's edges, so on a round limb it stays inside the body. Nothing of
  the site shows where it would overhang the body or where the body is too thin for skin, fat and muscle (the edge of a
  limb or the flank), so it never sticks out past the body's outline.
- **Blood**: bleeding wounds feed a shallow film on the skin: it spreads sideways, runs downhill and leaves a thin stain
  as it moves on; strong bleeds spurt. It runs on over the whole body, the bed (pads and frame) and the floor, with no
  visible boundary at the site or at a mesh edge. Breathing doesn't move it, and it follows a pull or a turn within a
  few frames. A flap's underside stays clean. Where a film faces down it drips onto whatever lies below, and skin
  pressed on the mattress hands its blood straight to the bed, so turning the patient carries it down onto the bed. A
  staple squeezes half the blood off its seam without wiping it clean. Irrigation from a syringe gathers briefly as a
  pool in the opening it lands in, tinted by the blood it washes out, and drains away. An open wound fills what's under
  it first, then spills over onto the skin: the cavity once it's cut through the muscle, else the cut itself, its blood
  resting on the first layer it hasn't cut through (the fat under a skin cut, the muscle once the fat is cut) and rising
  as it fills. A cut holds as much as it is long, gapes and is deep to that layer; suction and swabs drain it. Every
  wound that bleeds at all shows where, as a spot like a cut vessel (blood-red middle, dark bluish rim) that grows with
  how fast it bleeds and is never too small to see. A hole (puncture, bullet) shows it on the skin; a cut along a line
  shows none there, as the cut would split it across its lips. Inside the opening it sits on the wall, no bigger than
  that wall is high and never on the skin over it: a cut's own edge at the top of the fat's wall, just under the skin,
  where a lifted lip shows it; a vessel in the middle of its own layer's wall. Its blood runs down the wall below it in
  a narrow stream, widening a little, and blood carried onto the wall from the skin fades out within a centimeter of the
  lip, so a standing wall shows where it bleeds from. Blood pooled over a spot (the cut's own pool, or the cavity's
  where the cut reaches it) moves it up to the pool's surface so its top still shows, until the pool brims over the
  lips. Spots ride the tissue wherever it's pulled and go when the bleeding stops (a puncture's when it dries or is
  wiped). One under skin that isn't cut spreads a bruise over itself instead. Blood looks like a raised wet film: fresh
  red when thin, dark and glossy when thick, with a ragged edge whose rim catches the light.
- **Tool effects**: cautery and lighter smoke, bone dust from the saw, blood thrown up by the mallet, a flash and sparks
  at the defibrillator paddles with the body jerking, a bead of blood where a needle or catheter goes in (gauze or a pad
  wipes it away like other blood): on the site a spot on the skin like a bleed's, so it rides the skin when a cut is
  pulled open, elsewhere a small dome, and a thin stream of liquid squirted from a syringe, arcing from its tip and
  following it while pushes go on. Lasting marks (cuts, burns, stitches, ink, iodine, paddle marks) stay on the skin.
  Tools working in blood come away bloody at the tip; the sink washes it off. The IV catheter gets a film dressing.
- **Animation** is procedural and driven by synced game state, so it matches on every peer:
  - Patient: breathing at the respiration rate (the trunk and surgical site rise together), eyes open when conscious,
    jaw moves while talking, head tracks and flinches with pain, panic flails, seizures shake every joint. The limb a
    site is on moves too, the site riding it; a part a surgeon's hand holds stays still.
  - Surgeon: walk cycle from movement speed, collapse when passed out, lying on the side when knocked out, head tilt
    from camera pitch, two-bone IK arms, glove finger bones relax, wrap around a held tool and squeeze while using it.
    The glove's cuff follows the forearm, so a bent wrist stretches the glove over the sleeve.
  - Tools: jaws open and close, plungers push, stapler triggers squeeze, saw blades oscillate, lighter flame and cautery
    tip light up, defibrillator charge light blinks.
- **Sounds**: effects synthesized from noise, oscillators, filters and formants (tissue, tools, room tone loops, surgeon
  coughs and moans, patient groans/screams/breathing). The monitor beep is generated live, its pitch following SpO2 like
  a real pulse oximeter. Patient speech is subtitles; optional recorded lines can be dropped into `assets/audio/voice/`.

## Known limits

- Grips are four styles, not a pose per tool; a tool passes between the fingers rather than touching them exactly.
- Physics pushing of organs runs on the host only; clients see synced positions.
- Tilt and twist change tool orientation and tip position, but no target requires a specific twist.
- Late joining mid-surgery isn't supported. Both players join in the lobby.
- Blood films, drips and pools are simulated by each peer on its own, so they differ slightly between players.
