# Data formats

What designers edit in `data/`.
The game reads these files at startup through `Db`.

## Quirks (`data/quirks/*.md`)

Markdown, one `## id` heading per quirk, `- key: value` lines.
Any key can be overridden per variant with `key.variant`.
Icons are relative links, clickable from an editor.
`effects` is what code reads: `key=value, key=value`.
`_mult` keys multiply, other numbers add, text values are lists split by `|`.
`sites` limits a patient quirk to scenarios on those surgical sites (an aneurysm only where a great vessel runs).
`weight` sets how often a variant rolls against the others (1 by default), `label` what menus show for it in brackets.
A drug allergy is named on the chart 70% of the time; the unknown rest hides the drug until the lab's allergy screen,
and a tenth of those patients aren't allergic at all.
An `alternatives` drug (drugs.cfg) given to an allergy patient who could take the drug it stands in for costs points.

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

Rolling rules live in `src/Data/QuirkRoller.cs`: 1-3 surgeon quirks, with 3 at least one positive and one negative
(`mixed` counts as both), `exclusive` quirks alone.

## Scenarios (`data/scenarios/NN_id.cfg`)

`[scenario]` holds metadata and setup, `[patient]` the starting wounds, burns, internal wounds and cavity targets,
`[objectives]` the steps.
The file name gives the id (`03_appendectomy.cfg` -> `appendectomy`), the number the order.
`disabled=true` keeps a scenario out of the menu and lobby while its positive flow test is a known issue or broken; a
comment beside it names the test.

Objective step types (`src/Operation/ObjectiveChecks.cs`):
`sanitize, irrigate, iv, anesthesia, sedate, local_block, mark, incise, extract, close, close_internal, stop_bleeding, stabilize, calm, inject, defib, tourniquet, clamp, transfuse, flip, align, debride, graft, listen, comfort, wait, xray`.
`xray` is met once developed films show its `"target"` kind, or with `"clear": true` show none once none is left in the
patient.
Steps are requirements checked all the time, in any order.
`close, close_internal, stop_bleeding, stabilize` count only while their condition holds; the rest are milestones, done
once reached.
`"optional": true` steps give bonus points; `"hint": true` lists an optional one for Intern too.
`"after": [ids]` names steps (by `"id"`, else type) whose condition must hold when the step is first met (the tourniquet
still on, the block not worn off); otherwise it's spoiled and no longer counts.
Used only where the order matters medically: numbing before cutting or stitching, cleaning before opening, a tourniquet
before cutting a limb, heparin before clamping.
Nothing ends the surgery when the steps are done: the players sign the patient out (see Discharge), or the time limit
does it for them.
Then every required step must count for a success; each one that doesn't is scored as missed.
A step with `"or": {step}` also completes when that nested step's condition is met (hand stitch: a local block, or
general anesthesia for a lidocaine allergy).
Each step's `"label"` is its main task as the HUD lists it, written for a new player: what to do and with what.
Its subtasks, which only the easiest difficulty shows, come from its type's section of `data/objectives.cfg` (an
`extract` step's by its target's `remove_with`), or from its own `"subtasks"` list.
They're worked out for the patient when the surgery starts (`Subtasks.cs`, tokens explained in that file), and a line
starting with `{if.name}` is listed only while that condition holds for the patient (the muscle still cut open, the
heart stopped): the dose for the patient's weight in ml of the vial and the syringe for it, the first alternative of a
drug the chart says the patient is allergic to (a section's `allergy_instead` gives the subtasks for it), a lab screen
first for an allergy the chart doesn't name, and ordering from the nurse when the room has no such tool.
The host sends them only for the open steps up to the next required one, with the steps they come `"after"` that don't
hold yet.
A state step shows done only once the required steps before it count.
A drug step (`anesthesia`, `local_block`, `sedate`, `inject`) names its drug in `"drug"`, else its section's, and why
that one in `"reason"`, shown after its dose.
Every instruction matches the manual's wording for it.

Difficulty (`Settings.Difficulty`, per player, asked on the first start): **I'm just a baby** lists every main task,
optional ones marked `[optional]`, with the subtasks of the next ones to do; **Intern** the required main tasks and the
hints; **MD** none.
Both end with the discharge, which isn't a step (it ends the surgery) and has no subtasks.
Once the required steps count, "I'm just a baby" also lists aftercare before it as optional tasks: waking the patient
with flumazenil while they're under (`[wake]` in `objectives.cfg`), and taking out the IV and taping the hole while
either is left (`[remove_iv]`).
Debug mode adds the score, the latest scored actions and the debug toasts at any difficulty.
`anesthesia` is met once the patient is out (eyes closed) with a general anesthetic working, however deep: a right dose
of weaker drugs counts too.
A weaker patient may need more than the right dose to close their eyes (Alcoholic).
`sedate` is met once a sedative that isn't a general anesthetic (diazepam, morphine) works and has calmed the patient
below `max_panic` while they're still awake; a patient who was out at any point before (put under, too much sedative)
never meets it.
`irrigate` is met once no cut is dirty: glass leaves its track dirty.
A syringe's plunger pushed without the needle in (Use tool not held) squirts its liquid out, and it falls like any
liquid (`Patient.Spill()`), shown as a thin stream arcing from the needle tip to where it lands
(`ToolEffects.StreamPath()`, the same arc decides where it lands): landing in a cut it flushes it, clean after
`Patient.RinseMlPerCm` per cm of its length.
A track its glass is still in is shut and can't be rinsed.

Target `remove_with`: `clamp` (grab and pull out, `anchor` > 0 needs cutting or a slow pull first), `suction` (drain
`amount`, `refill` per second), `saw` / `smash` (bone).
A target's `depth` is meters under the skin.
In a limb it's kept inside the thin cavity between the muscle and the bones (`PatientBody.TargetDepth()`): its top under
the muscle and its middle no deeper than the floor, so it fits a heavy patient's thicker fat too.
A bone to saw there lies half sunk in the floor, in the limb bone's place.
An `embedded` target (glass) is stuck in through the skin and sticks out of it: it keeps its `depth` on any site and
comes with its own track, a puncture across it as deep as its tip reaches (`Patient.EmbeddedWound()`).
While it's in it holds the track shut (`CavityTarget.PlugHold`) and hurts unless the site is numb; pulled out, the track
bleeds.
One whose top lies under the skin is buried: jaws reach it only once its track is spread open
(`CavityTarget.SpreadToReach`, e.g. with the Gelpi retractor).
A target's `scale` sizes its model (1 by default).

## Other data

- `tools.cfg`: every item, its action and numbers. Backup tools reuse actions with worse numbers.
- `starter_kit.cfg`: the tools every surgery starts with. With a nurse, a scenario's `starting_tools` and `random_tools`
  add only what she can't fetch; without one (sidewalk, ambulance) they add their whole kit.
- `drugs.cfg`: effects, onset, duration, flags, dangerous combinations, blood types, dose per kg.
- `events.cfg`: random escalation events, weights and cooldowns.
- `scoring.cfg`: points and stress per action, star thresholds.
- `consequences.cfg`: post-op report rules (infection, burst staples, lawsuits).
- `dialogue/patient_lines.cfg`: patient speech by trigger and age.
- `manual/NN_id.txt`: in-game manual pages. A folder named like a page (`manual/17_conditions/`) holds its sub-pages:
  one entry per chronic condition (patient quirk with a chart line), tagged with the quirk id or `id.variant` for Divine
  knowledge. An entry's own folder holds a third level (`17_conditions/allergy/`: one page per allergen). General pages
  point to these entries instead of repeating them. Game numbers in pages are `{expression}`s worked out at load from
  class constants, `drug`, `tool` and `quirk` (see `ManualPage`), so doses, thresholds and chances follow the game. A
  data test rejects numbers with units written by hand.
- `audio.cfg`: sound id to file.

