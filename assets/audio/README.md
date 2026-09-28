# Audio

Sound ids are mapped to files in `data/audio.cfg`. Missing files are skipped, so add them one at a time.
Buses: `SFX`, `Voice`, `Music` (volume sliders in Settings).
The patient monitor beep, flatline and alarm are generated in `src/world/patient_monitor.gd` and need no files.

| File | What it should sound like |
|---|---|
| `sfx/tissue/cut_skin.ogg` | Scalpel through skin: soft, wet drag |
| `sfx/tissue/cut_deep.ogg` | Deeper cut through fat and fascia, a pop at the end |
| `sfx/tissue/tear_skin.ogg` | Skin tearing under tension, ragged |
| `sfx/tissue/suture_pull.ogg` | Thread pulled through skin |
| `sfx/tissue/tool_drop_flesh.ogg` | Metal dropped into an open body cavity |
| `sfx/tissue/blood_drip.ogg` / `blood_spurt.ogg` | Dripping and arterial spurt |
| `sfx/tissue/bone_crack.ogg` | Bone breaking |
| `sfx/tools/staple.ogg` | Surgical skin stapler click |
| `sfx/tools/office_staple.ogg` | Office stapler crunch |
| `sfx/tools/tape_rip.ogg` | Tape pulled off a roll |
| `sfx/tools/cautery_sizzle.ogg` | Electrocautery buzz and tissue sizzle |
| `sfx/tools/lighter_flick.ogg` | Lighter wheel and flame |
| `sfx/tools/suction_slurp.ogg` | Surgical suction gurgle |
| `sfx/tools/saw_bone.ogg` | Oscillating bone saw |
| `sfx/tools/mallet_hit.ogg` | Orthopedic mallet on bone |
| `sfx/tools/defib_charge.ogg` / `defib_shock.ogg` | Rising charge whine, thump |
| `sfx/tools/syringe_inject.ogg` | Plunger push |
| `sfx/tools/tool_drop_metal.ogg` / `tool_pickup.ogg` | Instrument on tile, instrument off tray |
| `sfx/room/nurse_bell.ogg` | Hotel desk bell |
| `sfx/room/nurse_delivery.ogg` | Cart wheels, tray set down |
| `sfx/room/fluorescent_buzz.ogg` | Loopable tube light hum |
| `sfx/room/body_fall.ogg` | Body hitting the floor |
| `sfx/room/page_turn.ogg` | Manual page turn |
| `sfx/room/ambulance_rumble.ogg` / `street_ambience.ogg` | Loopable environment beds |
| `sfx/surgeon/vomit.ogg`, `cough.ogg`, `sip.ogg`, `bump.ogg` | Surgeon body sounds |
| `voice/<trigger>_<index>.ogg` | Optional recorded patient lines, see `data/dialogue/patient_lines.cfg` |
