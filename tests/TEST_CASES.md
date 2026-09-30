# Test cases

This list is the source of truth for what the automated tests cover.
When a test changes, update its entry here in the same commit; when adding a feature, add its case here first.

Run everything with `./build.sh test`. `./build.sh` runs the tests before every export, and CI (`.github/workflows/tests.yml`) runs them on every push and pull request.
A test fails on any script error, any `FAIL:` line, or if it doesn't reach its "done" line. Logs go to `build/test-logs/`.

## Project

| ID | Case | Test |
|---|---|---|
| P1 | The project imports with no errors (scripts parse, resources load) | `run_tests.sh` import step |
| P2 | The Linux export builds and includes the data files | CI export step |
| P3 | The asset generator passes ruff and strict mypy | CI `assetgen` job |

## Models (`tests/models_test.gd`)

| ID | Case |
|---|---|
| M1 | The patient model has a skeleton with every bone the animator drives (trunk, neck, head, jaw, arms, legs) |
| M2 | The patient model has separate EyeL, EyeR and closed-Lids parts |
| M3 | The surgeon glove has a skeleton with Hand and three bones per finger and thumb |
| M4 | Every organ model and every target kind used by any scenario has a model file |
| M5 | Every model stays within its category's triangle budget (patient 52k, surgeon parts 16k, organs 7k, targets 5k, tools 6k, props 15k) |
| M6 | Every organ a site's anatomy uses has a model |
| M7 | Every tool model held in either glove, fitted by `data/grips.json`: nothing of the tool is inside the glove's fingers or palm (`tests/fit_grips.tscn` makes the fits) |
| M8 | Wherever the hand works (in front, out to the side, low, near), a held tool keeps the hand turned in: back of the hand up, or out to its own side for a fist round a handle, never twisted palm up |
| M9 | Empty and in every grip, with the arm stretched past its reach, folded up to the shoulder or reaching straight along the elbow's bend, both hands stay finite and the left glove is exactly the right one mirrored |
| M10 | Both gloves, empty and in every grip, from working spots to the arm's limits: the cuff follows the forearm, the end of the sleeve under it stays inside the glove, and the cuff hugs the sleeve (never more than 8 mm off it) |
| M11 | Imported materials retain texture channels, UV transforms, normal maps and metallic/roughness values without aliasing unrelated materials |
| M12 | Both hands have finite, non-degenerate poses at coincident, straight and pole-aligned arm targets for every grip |
| M13 | Scalpel and switchblade blades extend toward the game's -Z working tip, away from their handles |
| M14 | Contact audio reuses a loop per tool, caps simultaneous loops by priority, and fades stale contacts |

## Soft tissue (`tests/tissue_test.gd`)

| ID | Case |
|---|---|
| T1 | Intact skin has no gap |
| T2 | A full depth cut gapes on its own from skin tension and opens into the cavity |
| T3 | Skin tension alone never tears anything |
| T4 | A cut into the fat opens holes in the skin and fat layers, the muscle layer stays whole and the cavity stays closed |
| T5 | Pulling a cut edge 2 cm widens the gap without tearing |
| T6 | Pulling a cut edge 8 cm tears the skin |
| T7 | Only the host tears tissue: a client's sim never snaps springs itself |
| T8 | Thin skin (tear threshold ×0.5) doesn't tear from its own tension |
| T9 | Stitching along a whole cut into the fat closes the gap and the hole in the skin |
| T10 | A burst closure gapes again |
| T11 | The sim sleeps when nothing moves and a sleeping sim does no work |
| T12 | A jolt wakes the sim |
| T13 | A slow 3 cm pull on intact skin doesn't tear it, and skin 4 cm away follows by more than 8 mm |
| T14 | All skin that moved visibly since it settled is inside the simulated region |
| T15 | A cut through the muscle gapes wider than one into the fat and leaves the muscle open |
| T16 | Sewing the muscle along the cut closes the muscle layer and the cavity; the skin still gapes until stitched |
| T17 | A stitch closes a cut only where the edges meet: a tight one closes it, a loose one leaves the gap and the hole in the skin open |
| T18 | A client mirroring the host's snapped springs by their index ends with exactly the host's tissue topology, diagonal tears and a torn stitch included |
| T19 | Skin contact follows the deformed skin: higher where a grip lifts it, unchanged far from it, none over an open incision |
| T20 | Untouched skin over a round body (25 cm radius) settles when it's built, then stays put: it isn't shown simulated and sleeps |
| T21 | Where the site hangs off the body, its skin is never drawn and never shown simulated, even with a cut through it and a pull right next to it |
| T22 | Skin folded out of the drape's opening lies on the drape instead of passing through it; skin that starts under the drape stays under it |

## Every scenario (`tests/smoke_test.gd`)

Runs each of the 23 scenarios solo, with rolled surgeon and patient quirks and **all** run modifiers on at once:

hand_stitch, hand_stitch_child, appendectomy, bullet_muscle, sidewalk_stab, ambulance_bullet, open_fracture, slit_throat, knife_back, bullet_stomach, broken_ribs, lung_fluid, gangrene_amputation, burn_graft, nose_job, oscar_figurine, blocked_artery, heart_attack, colon_cancer, bullet_near_heart, leg_extension, brain_tumor, euthanasia.

For each scenario, with no script errors:

| ID | Case |
|---|---|
| S1 | The surgery scene builds and starts (room, patient, surgeons, tray) |
| S2 | Every tool on the tray is grabbed, lowered onto the site, worked at medium and full effort with its tool action held, and released |
| S3 | Every wound is stitched at loose, right and tight tension |
| S4 | Organs pushed out of place for 6 s bruise (organ handling damage) |
| S5 | Passing a tool to your other hand works |
| S6 | The chart correction arrives and the patient card reopens |
| S7 | The X-ray cart is pushed over, exposes, and its print develops and opens |
| S8 | Every event in `data/events.cfg` fires |
| S9 | Every drug is given directly, and saline through an IV |
| S10 | The patient is shocked four times with the defibrillator |
| S11 | Lab results, a nurse request and turning the patient (including a failed turn) work |
| S12 | Manual, patient card and nurse overlays open and close |
| S13 | The surgery finishes and the post-op report builds with a score |
| S14 | The defibrillator waits on its own cart in every room |
| S15 | A nurse delivery lands on the delivery tray (operating room) |
| S16 | A tool from the floor can't be sanitized until it's washed at the sink; wash then sanitize makes it sterile |
| S17 | Walking through the IV tubing at full speed rips the line out |
| S18 | Every tool effect plays; a tool in blood gets bloody, so does the glove holding it, and the sink washes both; a splash lands blood on the view |
| S19 | RMB picks up the tool under the hand and puts it down; LMB lowers and works it; the wheel sets its level; Shift steps through three zoom levels; the controls shown change while a hand key is held |
| S20 | The rolled tray holds the whole starter kit; forceps pick up a cotton pad, it soaks up iodine in the dish, sanitizes the skin and drops when the forceps are let go |
| S21 | The nurse takes one order at a time, the bell board shows it, the cooldown starts after the delivery |
| S22 | The patient has a plausible weight; a syringe draws from a vial; the right dose per kg counts once the needle comes out, a third of it doesn't; two vials mix in one syringe; a syringe dropped on the floor breaks |
| S23 | Wiping iodine with a soaked pad never takes more than 4 ms of one frame (no stutter; it used to take 7-20 ms every frame) |
| S24 | Skin won't close over a cut through open muscle, a tight stitch tears there, and after the muscle is sewn from inside the skin closes |
| S25 | Chest and belly: an H-shaped incision through the muscle, both flaps folded back without tearing, leaves no skin, fat or muscle over the top layer of organs (lungs and heart; liver, stomach and bowel) or the ribs; there are organs under the top layer |
| S26 | Forceps in the open chest or belly take hold of the top organ over a lower one; moving it aside shows the lower one |
| S27 | Chest, belly, arms, legs and shoulder: bones lie right under the muscle inside the cavity (ribs and breastbone, the lower rib margin, limb bones); under a deep cut the bone is the first thing inside, and the blade grates on it |
| S28 | The heart beats with the pulse, the lungs swell with each breath, and the heart lies still in asystole |
| S29 | Holding Inspect brings a syringe up in front of the eyes, across the view, tick marks toward them; the liquid and the plunger show exactly how many ml it holds |
| S30 | A tourniquet pressed onto a thigh wraps around it as a snug band (not lying on top), leaves the hand, and taking it off loosens it |
| S31 | Tools lie on the tray at the start without sinking into it; lowered onto it with full effort, neither the tool nor the glove goes into it; put down, it settles on top (every kind of tool in the first scenario, one in the others) |

## Co-op over the network (`tests/net_test.gd`)

Two real game processes on localhost, one hosting and one joining.

| ID | Case |
|---|---|
| N1 | The client joins, both ready up, and the surgery starts on both with two surgeons |
| N2 | The client grabs a cutting tool, lowers it and cuts along the blade edge; the host simulates the cut |
| N3 | The client hands the tool across the table to the host's hand |
| N4 | Host and client end with the same painted wound map |
| N5 | Host and client end with the same cut tissue: the same severed springs and the same topology hash (`TissueSim.topology_hash()`) |
| N6 | Spotty connection: the client process is frozen for 10 s mid-surgery (past ENet's default timeout). Nobody gets disconnected, both are still in the same surgery afterwards, and the host paused the silent player's tool while they were gone (`tests/net_stall_test.gd`) |

## Visual checks (not automated)

`tests/screenshot.gd` renders views for a person to look at; it needs a display (`xvfb-run`) and isn't part of the pass/fail run.

| ID | View |
|---|---|
| V1 | First person view and looking down at the patient |
| V2 | Surgical site close-up: cuts, burns, bruise, marker, blood, iodine |
| V3 | Retracted incision showing skin, fat and muscle layers |
| V4 | Room overview and instrument tray |
| V5 | Hands holding a scalpel and forceps |
| V6 | Menus (`--menus`) |
| V7 | Zoomed-in first person view (`02b_zoomed`) |
| V9 | Hands working over the thighs: gloves and forearms rest on the legs, nothing sinks in (`08b_hands_on_legs`) |
| V8 | Every tool held in the right hand, or the left with `--left`, from both sides and from the eyes (`tests/grip_gallery.tscn`) |
| V10 | `--anatomy`: the chest or belly opened wide (from above and first person), a top organ held aside, a limb cut to the bone, a tourniquet on the thigh, a syringe held up to read |
