# Global illumination

Room environments enable SDFGI on Forward+. Four cascades with 10 cm cells cover the operating theatre, ambulance
and sidewalk. Indoor environments ignore sky lighting. The existing ambient fill remains the fallback on Mobile
and Compatibility, where SDFGI is unavailable.

Only the fixed room shell contributes to SDFGI's static field. Surgery opts geometry out of static GI when it enters
the scene tree, including later tool deliveries and tissue geometry; `Room.BuildShell` opts its architecture back
in. Moving objects still receive indirect light. This avoids stale silhouettes from moving patients, tools and
stations; movable objects do not contribute bounced light or indirect occlusion. Direct lighting and shadows stay
live, including flickering lights.

## Reproduce the captures and comparison

`./build.py setup` builds and imports the project. The focused regression is:

```bash
./build.py test --tag room_lighting --with-key-frames
./build.py screenshot_review --focused
```

These deterministic Compatibility captures verify the ambient fallback; they do not render SDFGI. To see SDFGI,
use the Forward+ capture mode:

```bash
RENDERER=forward_plus SHOTS_ARGS='--lighting' ./build.py shots appendectomy build/gi/or
```

The same capture works with `ambulance_bullet` and `sidewalk_stab`. Each produces room, patient and station views,
each with GI off/on. The simulation, shader clock and HUD are frozen, film grain is removed and first-person arms
are hidden in these external views.

For GPU timings, launch the capture scene on the machine's actual display instead of `build.py shots`' virtual
display. Use the Godot executable provisioned in `.tools/`, with `.tools/dotnet` on `PATH` and as `DOTNET_ROOT` when
using the bundled SDK:

```bash
GODOT --path . --rendering-method forward_plus --audio-driver Dummy --resolution 1920x1080 \
  res://tests/Support/Screenshot.tscn -- \
  --scenario=appendectomy --out=build/gi/or --lighting --benchmark
```

`performance.csv` identifies the adapter, renderer and resolution. Each view runs four passes (off/on/on/off),
180 warm-up frames then 240 samples per pass. V-sync and the frame cap are disabled. GPU timings come from
`RenderingServer.ViewportGetMeasuredRenderTimeGpu`, not FPS or CPU submission time. Separate wall-frame timings
include scheduling/presentation delays. GPU timestamps must be nonzero. These are steady-state paused-scene
comparisons; they exclude SDFGI startup and do not establish the gameplay worst-frame budget. Use
`./build.py full_performance_run` for the full moving gameplay regression on the native display.

The local comparison sheets, original PNGs and CSVs live in `build/gi/`. They are generated evidence and are not
part of the deterministic JPEG baseline in `tests/KeyFrames`.

Godot documents the contribution/receiving distinction in
[GeometryInstance3D GI modes](https://docs.godotengine.org/en/stable/classes/class_geometryinstance3d.html#enum-geometryinstance3d-gimode)
and the procedural/static constraints in
[Using SDFGI](https://docs.godotengine.org/en/stable/tutorials/3d/global_illumination/using_sdfgi.html).

## Local GPU result — 16 October 2026

NVIDIA GeForce RTX 4080 Laptop GPU, driver 615.71.09, Vulkan Forward+, 1920×1080, native desktop display.
Mean of the two pass medians per view, then averaged equally across room/patient/stations:

| Environment | GI off | GI on | Added GPU time | Increase |
|---|---:|---:|---:|---:|
| Operating room | 2.753 ms | 3.543 ms | 0.790 ms | 28.7% |
| Ambulance | 2.051 ms | 2.732 ms | 0.681 ms | 33.2% |
| Sidewalk | 1.776 ms | 2.436 ms | 0.660 ms | 37.1% |

8,640 measured frames across nine viewpoints. Raw pass statistics, including GPU p95/max and wall-frame
median/p95/max, are in `build/gi/{or,ambulance,sidewalk}/performance.csv`; `build/gi/report.md` links comparison
sheets. These values describe the fixed-shell implementation and exclude startup. They do not imply a full
gameplay 16 ms worst-frame pass. `./build.py full_performance_run --tag room_lighting` passed the affected
lighting suite on the native GPU; the deterministic suite and lint also passed.
