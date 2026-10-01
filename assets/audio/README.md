# Audio

Every sound here is synthesized by `tools/assetgen/sounds.py` from noise, oscillators and filters
(run `python -m tools.assetgen` to rebuild). Ids map to files in `data/audio.cfg`; replace a file to replace a sound.
Buses: `SFX`, `Voice`, `Music` (volume sliders in Settings).

- `sfx/tissue/`: cuts, tears, suture pulls, blood, bone cracks.
- `sfx/tools/`: staplers, tape, cautery, lighter, suction, saws, mallet, defibrillator, syringe, drops.
- `sfx/room/`: nurse bell and cart, room tone loops (fluorescent hum, ambulance, street), body fall, page turn.
- `sfx/surgeon/`: vomit, cough, sip, bump.
- `sfx/patient/`: groan, scream, panicked breathing (formant synthesis; spoken lines are subtitles).

The patient monitor beep, flatline and alarm are generated live in `src/world/patient_monitor.gd`
(pitch follows SpO2 like a real pulse oximeter).
Continuous blade, swab and suction beds are loop-safe; tool contact and movement set their live level.
Optional recorded patient lines can go in `voice/<trigger>_<index>.ogg`, see `data/dialogue/patient_lines.cfg`.
