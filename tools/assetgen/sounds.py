"""Base sound effects, synthesized from noise, oscillators and filters. Writes 16-bit mono WAV files.

Every id in data/audio.cfg is produced here. Ambient beds are made loop-safe by crossfading their tail into the head.
"""

from __future__ import annotations

import wave
from collections.abc import Callable, Iterable
from pathlib import Path

import numpy as np
from numpy.typing import NDArray
from scipy import signal

RATE = 44100
AUDIO_DIR = Path(__file__).resolve().parents[2] / "assets" / "audio" / "sfx"
Wave = NDArray[np.float64]
rng = np.random.default_rng(1337)


def t(seconds: float) -> Wave:
    return np.arange(int(seconds * RATE)) / RATE


def noise(seconds: float) -> Wave:
    return rng.uniform(-1.0, 1.0, int(seconds * RATE))


def brown(seconds: float) -> Wave:
    walk = np.cumsum(noise(seconds))
    walk -= signal.savgol_filter(walk, 4001, 1)
    return np.asarray(walk / (np.abs(walk).max() + 1e-9), dtype=np.float64)


def band(x: Wave, low: float, high: float, order: int = 2) -> Wave:
    b, a = signal.butter(order, [low / (RATE / 2), min(high / (RATE / 2), 0.99)], btype="band")
    return np.asarray(signal.lfilter(b, a, x), dtype=np.float64)


def mix(parts: Iterable[Wave]) -> Wave:
    """Sums waves of equal length."""
    return np.asarray(np.sum(np.stack(list(parts)), axis=0), dtype=np.float64)


def lowpass(x: Wave, cutoff: float) -> Wave:
    b, a = signal.butter(2, cutoff / (RATE / 2), btype="low")
    return np.asarray(signal.lfilter(b, a, x), dtype=np.float64)


def highpass(x: Wave, cutoff: float) -> Wave:
    b, a = signal.butter(2, cutoff / (RATE / 2), btype="high")
    return np.asarray(signal.lfilter(b, a, x), dtype=np.float64)


def env(n: int, attack: float, decay: float, curve: float = 3.0) -> Wave:
    """Attack in seconds, then exponential-ish decay over the rest."""
    a = int(attack * RATE)
    out = np.ones(n)
    if a > 0:
        out[:a] = np.linspace(0.0, 1.0, a)
    tail = np.linspace(0.0, 1.0, n - a)
    out[a:] = np.exp(-tail * curve * (len(tail) / RATE) / max(decay, 1e-3))
    return out


def tone(freq: float | Wave, seconds: float, shape: str = "sine") -> Wave:
    tt = t(seconds)
    phase = 2 * np.pi * np.cumsum(np.broadcast_to(freq, tt.shape)) / RATE
    if shape == "saw":
        return np.asarray(signal.sawtooth(phase), dtype=np.float64)
    if shape == "square":
        return np.asarray(signal.square(phase), dtype=np.float64)
    return np.asarray(np.sin(phase), dtype=np.float64)


def partials(freqs: list[float], decays: list[float], seconds: float) -> Wave:
    tt = t(seconds)
    return mix(np.sin(2 * np.pi * f * tt) * np.exp(-tt / d) for f, d in zip(freqs, decays, strict=True)) / len(freqs)


def place(base: Wave, clip: Wave, at: float, gain: float = 1.0) -> Wave:
    start = int(at * RATE)
    end = min(len(base), start + len(clip))
    base[start:end] += clip[: end - start] * gain
    return base


def loopable(x: Wave, fade: float = 0.5) -> Wave:
    """Crossfades the tail into the head so the file loops without a click."""
    n = int(fade * RATE)
    ramp = np.linspace(0.0, 1.0, n)
    head = x[:n] * ramp + x[-n:] * (1.0 - ramp)
    return np.concatenate([head, x[n:-n]])


def normalize(x: Wave, peak: float = 0.8) -> Wave:
    return np.asarray(x / (np.abs(x).max() + 1e-9) * peak, dtype=np.float64)


# --- Sounds -------------------------------------------------------------------------------------------


def cut_skin() -> Wave:
    x = band(noise(0.35), 1500, 6000) * env(int(0.35 * RATE), 0.03, 0.15)
    squelch = band(noise(0.35), 200, 700) * (0.5 + 0.5 * np.sin(2 * np.pi * 18 * t(0.35))) * env(int(0.35 * RATE), 0.05, 0.1)
    return np.asarray(x + squelch * 0.6, dtype=np.float64)


def cut_deep() -> Wave:
    x = band(noise(0.6), 400, 3000) * env(int(0.6 * RATE), 0.05, 0.3)
    pop = band(noise(0.05), 200, 1200) * env(int(0.05 * RATE), 0.001, 0.02)
    return place(x, pop, 0.5, 1.5)


def contact_cut() -> Wave:
    """A quiet, unaccented blade-on-tissue bed; movement controls its level in game."""
    seconds = 1.2
    local = np.random.default_rng(4101)
    rub = band(local.uniform(-1.0, 1.0, int(seconds * RATE)), 1100, 4800) * (0.7 + 0.3 * np.sin(2 * np.pi * 5.0 * t(seconds)))
    wet = band(local.uniform(-1.0, 1.0, int(seconds * RATE)), 180, 700) * 0.3
    return loopable(rub * 0.55 + wet, 0.2)


def contact_swab() -> Wave:
    """Soft fabric rubbing wet skin, without the attack of a one-shot wipe."""
    seconds = 1.2
    local = np.random.default_rng(4102)
    weave = band(local.uniform(-1.0, 1.0, int(seconds * RATE)), 400, 2400) * (0.65 + 0.35 * np.sin(2 * np.pi * 3.0 * t(seconds)))
    return loopable(weave + band(local.uniform(-1.0, 1.0, int(seconds * RATE)), 120, 500) * 0.18, 0.2)


def contact_suction() -> Wave:
    """Steady air draw with small irregular bubbles."""
    seconds = 1.4
    local = np.random.default_rng(4103)
    x = band(local.uniform(-1.0, 1.0, int(seconds * RATE)), 250, 1450) * 0.55
    for at in local.uniform(0.15, seconds - 0.15, 18):
        bubble = tone(np.linspace(430, 850, int(0.025 * RATE)), 0.025) * env(int(0.025 * RATE), 0.002, 0.016)
        place(x, bubble, float(at), 0.12)
    return loopable(x, 0.15)


def tear_skin() -> Wave:
    x = np.zeros(int(0.7 * RATE))
    for at in np.sort(rng.uniform(0.0, 0.6, 40)):
        crack = band(noise(0.02), 800, 5000) * env(int(0.02 * RATE), 0.0005, 0.006)
        place(x, crack, at, rng.uniform(0.3, 1.0))
    return x + band(noise(0.7), 150, 600) * env(int(0.7 * RATE), 0.1, 0.4) * 0.5


def suture_pull() -> Wave:
    sweep = np.linspace(5000, 1500, int(0.5 * RATE))
    x = noise(0.5) * env(int(0.5 * RATE), 0.08, 0.3)
    return np.asarray(band(x, 1000, 7000) * (0.6 + 0.4 * np.sin(2 * np.pi * np.cumsum(sweep / 40) / RATE)), dtype=np.float64)


def staple() -> Wave:
    click = highpass(noise(0.01), 3000) * env(int(0.01 * RATE), 0.0005, 0.003)
    ring = partials([3100, 4700, 6900], [0.05, 0.04, 0.03], 0.15)
    return place(ring * 0.6, click, 0.0, 1.0)


def office_staple() -> Wave:
    thud = tone(110, 0.25) * env(int(0.25 * RATE), 0.002, 0.05)
    crunch = band(noise(0.06), 500, 4000) * env(int(0.06 * RATE), 0.001, 0.02)
    return place(thud * 0.6 + partials([2600, 3900], [0.06, 0.04], 0.25) * 0.4, crunch, 0.01, 1.0)


def tape_rip() -> Wave:
    n = int(0.6 * RATE)
    stick_slip = (np.sin(2 * np.pi * np.cumsum(np.linspace(40, 180, n)) / RATE) > 0.3).astype(float)
    return band(noise(0.6), 800, 6000) * lowpass(stick_slip, 400) * env(n, 0.05, 0.4)


def cautery_sizzle() -> Wave:
    buzz = mix(tone(120 * k, 1.0, "square") / k for k in (1, 2, 3)) * 0.2
    crackle = np.zeros(RATE)
    for at in rng.uniform(0.0, 0.95, 120):
        place(crackle, highpass(noise(0.004), 2000), at, rng.uniform(0.2, 0.8))
    hiss = band(noise(1.0), 3000, 9000) * 0.3
    return loopable(buzz + crackle + hiss, 0.1)


def lighter_flick() -> Wave:
    scrape = band(noise(0.08), 2000, 8000) * env(int(0.08 * RATE), 0.005, 0.03)
    whoosh = lowpass(noise(0.5), 600) * env(int(0.5 * RATE), 0.05, 0.3)
    return place(whoosh * 1.5, scrape, 0.0, 1.0)


def suction_slurp() -> Wave:
    x = band(noise(0.8), 300, 1500) * 0.4
    for at in rng.uniform(0.0, 0.7, 14):
        bubble = tone(np.linspace(rng.uniform(300, 700), 1200, int(0.03 * RATE)), 0.03) * env(int(0.03 * RATE), 0.002, 0.012)
        place(x, bubble, at, 0.8)
    return x * env(len(x), 0.05, 0.6, 1.0)


def saw_bone() -> Wave:
    strokes = 0.55 + 0.45 * np.sin(2 * np.pi * 7 * t(1.0))
    grind = band(noise(1.0), 1200, 5000) * strokes
    motor = band(tone(180 + 8 * np.sin(2 * np.pi * 7 * t(1.0)), 1.0, "saw"), 150, 2500) * 0.4
    return loopable(grind + motor, 0.1)


def mallet_hit() -> Wave:
    thud = tone(np.linspace(140, 70, int(0.3 * RATE)), 0.3) * env(int(0.3 * RATE), 0.001, 0.06)
    crack = band(noise(0.03), 1500, 7000) * env(int(0.03 * RATE), 0.0005, 0.008)
    return place(thud, crack, 0.0, 0.8) + partials([1800, 2700], [0.04, 0.03], 0.3) * 0.2


def defib_charge() -> Wave:
    n = int(2.0 * RATE)
    whine = tone(np.geomspace(700, 3200, n), 2.0) * np.linspace(0.2, 1.0, n)
    return whine * 0.5 + band(noise(2.0), 5000, 9000) * 0.03


def defib_shock() -> Wave:
    thump = tone(np.linspace(90, 40, int(0.4 * RATE)), 0.4) * env(int(0.4 * RATE), 0.002, 0.08)
    zap = band(noise(0.06), 1000, 9000) * env(int(0.06 * RATE), 0.0005, 0.02)
    return place(thump, zap, 0.0, 1.2)


def syringe_inject() -> Wave:
    hiss = band(noise(0.4), 2000, 6000) * env(int(0.4 * RATE), 0.05, 0.3) * 0.4
    squeak = tone(np.linspace(900, 1300, int(0.15 * RATE)), 0.15) * env(int(0.15 * RATE), 0.01, 0.1) * 0.3
    return place(hiss, squeak, 0.02)


def tool_drop_metal() -> Wave:
    x = np.zeros(int(0.9 * RATE))
    for i, (at, gain) in enumerate(((0.0, 1.0), (0.14, 0.5), (0.24, 0.25))):
        clang = partials([2200 + 90 * i, 3350, 5100, 7300], [0.12, 0.09, 0.06, 0.04], 0.5)
        place(x, clang, at, gain)
        place(x, highpass(noise(0.01), 3000) * 0.5, at, gain)
    return x


def tool_drop_flesh() -> Wave:
    thud = lowpass(noise(0.3), 500) * env(int(0.3 * RATE), 0.002, 0.08)
    splat = band(noise(0.15), 400, 2000) * env(int(0.15 * RATE), 0.005, 0.05)
    return thud * 1.5 + place(np.zeros(len(thud)), splat, 0.01)


def tool_pickup() -> Wave:
    return partials([3900, 5800], [0.03, 0.02], 0.1) + highpass(noise(0.1), 4000) * env(int(0.1 * RATE), 0.001, 0.01) * 0.3


def blood_drip() -> Wave:
    n = int(0.12 * RATE)
    return tone(np.geomspace(1400, 500, n), 0.12) * env(n, 0.001, 0.03)


def blood_spurt() -> Wave:
    x = np.zeros(int(1.0 * RATE))
    for at in (0.0, 0.33, 0.66):
        place(x, band(noise(0.22), 300, 2500) * env(int(0.22 * RATE), 0.01, 0.1), at)
    return x


def bone_crack() -> Wave:
    x = np.zeros(int(0.3 * RATE))
    for at in np.sort(rng.uniform(0.0, 0.06, 8)):
        place(x, band(noise(0.015), 1500, 9000) * env(int(0.015 * RATE), 0.0002, 0.004), at, rng.uniform(0.5, 1.0))
    return x + lowpass(noise(0.3), 300) * env(int(0.3 * RATE), 0.001, 0.05) * 0.8


def nurse_bell() -> Wave:
    f = 2350.0
    ring = partials([f, f * 2.76, f * 5.4, f * 8.9], [0.9, 0.5, 0.25, 0.12], 1.6)
    strike = highpass(noise(0.005), 3000)
    return place(ring, strike, 0.0, 0.5)


def nurse_delivery() -> Wave:
    rattle = band(noise(1.2), 200, 1500) * (0.5 + 0.5 * np.abs(np.sin(2 * np.pi * 9 * t(1.2)))) * env(int(1.2 * RATE), 0.2, 0.8, 1.0)
    squeak = tone(1800 + 200 * np.sin(2 * np.pi * 3 * t(0.4)), 0.4) * env(int(0.4 * RATE), 0.05, 0.2) * 0.2
    clack = partials([1200, 2600], [0.05, 0.03], 0.2)
    return place(place(rattle, squeak, 0.3), clack, 1.0)


def fluorescent_buzz() -> Wave:
    hum = mix(tone(120 * k, 2.0) / k**1.3 for k in range(1, 8))
    return normalize(hum + band(noise(2.0), 4000, 9000) * 0.05, 0.5)


def vomit() -> Wave:
    retch = band(tone(110 + 20 * np.sin(2 * np.pi * 4 * t(0.7)), 0.7, "saw"), 200, 1800) * env(int(0.7 * RATE), 0.1, 0.4)
    splash = lowpass(noise(0.6), 1500) * env(int(0.6 * RATE), 0.01, 0.2)
    return place(np.concatenate([retch, np.zeros(int(0.6 * RATE))]), splash, 0.6, 1.0)


def cough() -> Wave:
    x = np.zeros(int(0.8 * RATE))
    for at in (0.0, 0.32):
        burst = band(noise(0.25), 300, 3000) * env(int(0.25 * RATE), 0.005, 0.08)
        voice = band(tone(150, 0.25, "saw"), 200, 1500) * env(int(0.25 * RATE), 0.005, 0.06) * 0.4
        place(x, burst + voice, at)
    return x


def body_fall() -> Wave:
    thud = lowpass(noise(0.7), 250) * env(int(0.7 * RATE), 0.003, 0.15)
    return thud * 2.0 + place(np.zeros(len(thud)), tool_drop_metal()[: int(0.4 * RATE)] * 0.3, 0.05)


def sip() -> Wave:
    n = int(0.35 * RATE)
    return np.asarray(band(noise(0.35), 1500, 6000) * env(n, 0.05, 0.15) * (0.5 + 0.5 * np.sin(2 * np.pi * 25 * t(0.35))), dtype=np.float64)


def page_turn() -> Wave:
    n = int(0.45 * RATE)
    return highpass(noise(0.45), 1500) * np.asarray(np.sin(np.linspace(0, np.pi, n)) ** 2, dtype=np.float64)


def bump() -> Wave:
    return lowpass(noise(0.15), 400) * env(int(0.15 * RATE), 0.002, 0.04)


def ambulance_rumble() -> Wave:
    engine = mix(tone(34 * k + 0.5 * np.sin(2 * np.pi * 0.2 * t(6.0)), 6.0, "saw") / k for k in (1, 2, 3)) * 0.3
    road = lowpass(brown(6.0), 300)
    return loopable(normalize(band(engine, 25, 400) + road, 0.6), 0.8)


def street_ambience() -> Wave:
    traffic = lowpass(brown(8.0), 500) * 0.7
    hiss = band(noise(8.0), 800, 4000) * 0.05
    horn = band(tone(420, 0.5, "square"), 300, 2000) * env(int(0.5 * RATE), 0.02, 0.4) * 0.1
    return loopable(normalize(place(traffic + hiss, horn, 3.5), 0.5), 1.0)


def xray_expose() -> Wave:
    n = int(2.5 * RATE)
    hum = mix(tone(100 * k, 2.5) / k for k in (1, 2, 3, 5)) * np.minimum(np.linspace(0, 6, n), 1.0) * 0.4
    beep = tone(1000, 0.3) * env(int(0.3 * RATE), 0.01, 0.25)
    clunk = lowpass(noise(0.1), 400) * env(int(0.1 * RATE), 0.001, 0.03)
    return place(place(hum, beep, 2.1, 0.6), clunk, 2.4, 1.0)


def print_whir() -> Wave:
    n = int(1.5 * RATE)
    motor = band(tone(260 + 40 * np.sin(2 * np.pi * 3 * t(1.5)), 1.5, "saw"), 200, 3000) * 0.3
    paper = highpass(noise(1.5), 2500) * 0.15
    return (motor + paper) * env(n, 0.05, 1.2, 0.5)


def _voice(pitch: Wave, formants: tuple[float, float, float], seconds: float, breathiness: float) -> Wave:
    """Glottal buzz through three vowel formants, plus breath noise."""
    glottal = tone(pitch, seconds, "saw") + noise(seconds) * breathiness
    return mix(band(glottal, f * 0.85, f * 1.15) * g for f, g in zip(formants, (1.0, 0.6, 0.3), strict=True))


def patient_groan() -> Wave:
    n = int(1.2 * RATE)
    pitch = 120 + 25 * np.sin(np.linspace(0, np.pi, n)) + 4 * np.sin(2 * np.pi * 6 * t(1.2))
    return _voice(pitch, (600, 1000, 2400), 1.2, 0.3) * env(n, 0.15, 0.5, 1.5)


def patient_scream() -> Wave:
    n = int(1.1 * RATE)
    pitch = np.concatenate([np.linspace(260, 520, n // 3), 520 + 30 * np.sin(2 * np.pi * 7 * t(1.1)[: n - n // 3])])
    return _voice(pitch, (850, 1300, 2800), 1.1, 0.5) * env(n, 0.04, 0.6, 1.2)


def patient_breath() -> Wave:
    x = np.zeros(int(2.0 * RATE))
    for at in (0.0, 0.5, 1.0, 1.5):
        gasp = band(noise(0.35), 500, 3500) * np.sin(np.linspace(0, np.pi, int(0.35 * RATE))) ** 2
        place(x, gasp, at, 0.7 if at % 1.0 == 0 else 0.45)
    return loopable(x, 0.05)


def sink_water() -> Wave:
    """Tap running into a steel basin: broadband hiss with a gurgling wobble."""
    wobble = 0.75 + 0.25 * np.sin(2 * np.pi * 5.3 * t(1.6)) * np.sin(2 * np.pi * 0.7 * t(1.6))
    return np.asarray(band(noise(1.6), 500, 6000) * wobble * env(int(1.6 * RATE), 0.08, 0.4, 1.0), dtype=np.float64)


def cable_yank() -> Wave:
    """A foot catching the IV line: a rattle of the stand and a rubbery snap."""
    rattle = band(noise(0.35), 900, 5000) * env(int(0.35 * RATE), 0.002, 0.25)
    snap = tone(np.linspace(420, 90, int(0.12 * RATE)), 0.12) * env(int(0.12 * RATE), 0.001, 0.08)
    x = rattle * 0.6
    place(x, snap, 0.02, 0.9)
    return x


def glass_break() -> Wave:
    """A syringe hitting the floor: a sharp crack, then a scatter of high tinkling shards."""
    x = highpass(noise(0.03), 2500) * env(int(0.03 * RATE), 0.001, 0.02) * 1.2
    x = np.concatenate([x, np.zeros(int(0.67 * RATE))])
    for _ in range(14):
        shard = partials(list(rng.uniform(3500, 9000, 3)), [0.03, 0.02, 0.015], 0.12)
        place(x, shard, float(rng.uniform(0.005, 0.45)), float(rng.uniform(0.2, 0.7)))
    return x


SOUNDS: dict[str, tuple[str, Callable[[], Wave]]] = {
    "cut_skin": ("tissue", cut_skin),
    "cut_deep": ("tissue", cut_deep),
    "tear_skin": ("tissue", tear_skin),
    "suture_pull": ("tissue", suture_pull),
    "tool_drop_flesh": ("tissue", tool_drop_flesh),
    "blood_drip": ("tissue", blood_drip),
    "blood_spurt": ("tissue", blood_spurt),
    "bone_crack": ("tissue", bone_crack),
    "staple": ("tools", staple),
    "office_staple": ("tools", office_staple),
    "tape_rip": ("tools", tape_rip),
    "cautery_sizzle": ("tools", cautery_sizzle),
    "lighter_flick": ("tools", lighter_flick),
    "suction_slurp": ("tools", suction_slurp),
    "saw_bone": ("tools", saw_bone),
    "mallet_hit": ("tools", mallet_hit),
    "defib_charge": ("tools", defib_charge),
    "defib_shock": ("tools", defib_shock),
    "syringe_inject": ("tools", syringe_inject),
    "tool_drop_metal": ("tools", tool_drop_metal),
    "tool_pickup": ("tools", tool_pickup),
    "nurse_bell": ("room", nurse_bell),
    "nurse_delivery": ("room", nurse_delivery),
    "fluorescent_buzz": ("room", fluorescent_buzz),
    "body_fall": ("room", body_fall),
    "page_turn": ("room", page_turn),
    "ambulance_rumble": ("room", ambulance_rumble),
    "street_ambience": ("room", street_ambience),
    "vomit": ("surgeon", vomit),
    "cough": ("surgeon", cough),
    "sip": ("surgeon", sip),
    "bump": ("surgeon", bump),
    "xray_expose": ("room", xray_expose),
    "print_whir": ("room", print_whir),
    "patient_groan": ("patient", patient_groan),
    "patient_scream": ("patient", patient_scream),
    "patient_breath": ("patient", patient_breath),
    # New sounds go last: they share one random generator, so earlier sounds stay the same.
    "sink_water": ("room", sink_water),
    "cable_yank": ("room", cable_yank),
    "glass_break": ("tools", glass_break),
    "contact_cut": ("tissue", contact_cut),
    "contact_swab": ("tissue", contact_swab),
    "contact_suction": ("tools", contact_suction),
}


def write_wav(path: Path, samples: Wave) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    data = (np.clip(normalize(samples), -1.0, 1.0) * 32767).astype(np.int16)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(data.tobytes())


def build() -> list[Path]:
    paths = []
    for sound_id, (group, make) in SOUNDS.items():
        path = AUDIO_DIR / group / f"{sound_id}.wav"
        write_wav(path, make())
        paths.append(path)
    return paths
