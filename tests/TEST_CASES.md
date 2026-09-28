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
| T9 | Stitching along a whole cut closes the gap and the hole in the skin |
| T10 | A burst closure gapes again |
| T11 | The sim sleeps when nothing moves and a sleeping sim does no work |
| T12 | A jolt wakes the sim |

## Every scenario (`tests/smoke_test.gd`)

Runs each of the 23 scenarios solo, with rolled surgeon and patient quirks and **all** run modifiers on at once:

hand_stitch, hand_stitch_child, appendectomy, bullet_muscle, sidewalk_stab, ambulance_bullet, open_fracture, slit_throat, knife_back, bullet_stomach, broken_ribs, lung_fluid, gangrene_amputation, burn_graft, nose_job, oscar_figurine, blocked_artery, heart_attack, colon_cancer, bullet_near_heart, leg_extension, brain_tumor, euthanasia.

For each scenario, with no script errors:

| ID | Case |
|---|---|
| S1 | The surgery scene builds and starts (room, patient, surgeons, tray) |
| S2 | Every tool on the tray is grabbed, used on the site at normal and deep pressure, and released |
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

## Co-op over the network (`tests/net_test.gd`)

Two real game processes on localhost, one hosting and one joining.

| ID | Case |
|---|---|
| N1 | The client joins, both ready up, and the surgery starts on both with two surgeons |
| N2 | The client grabs a cutting tool and cuts the patient; the host simulates the cut |
| N3 | The client hands the tool across the table to the host's hand |
| N4 | Host and client end with the same painted wound map |
| N5 | Host and client end with the same cut tissue (same number of severed springs) |

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
