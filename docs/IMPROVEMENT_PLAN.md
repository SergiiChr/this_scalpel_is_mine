# Visual and physical fidelity plan

Target: the review's restrained clinical-horror concept: readable anatomy, matte blue gloves, cloth sleeves,
slender steel tools, warm skin midtones, coherent wet tissue and a cool hospital environment.
Preserve the procedural asset workflow, model/bone contracts, two-hand controls and host-authoritative surgery.

## Cost-ordered implementation

Estimates include implementation, inspection and tests in a prepared environment. They are rough token ranges,
not measured usage, a spending limit or a promise. Art iteration and debugging can dominate the cost.

| Order | Complete work package | Estimated tokens | Status |
|---|---|---:|---|
| 1 | Rig/pose bug fixes, glove/sleeve separation, stable mirrored grips and visual review tooling | 3–6k | Done in evaluation tranche |
| 2 | Material preservation/families, calibrated lighting, restrained outlines and grading | 5–10k | Done in evaluation tranche |
| 3 | Tissue correctness: residual stitch gaps, exact tear replication, deformed contact queries | 8–15k | Done in evaluation tranche |
| 4 | Hero geometry: glove deformation topology, patient landmarks, tool detail and contact poses | 15–30k | Planned |
| 5 | Continuous contact-driven tool audio, mix priorities, deformation-aware blood flow | 15–30k | Planned |
| 6 | Incision topology, continuous wound walls, independent layer response and local organ deformation | 30–60k+ | Planned |

The first evaluation tranche covers packages 1–3. It also corrects the procedural scalpel and switchblade
blade orientation, then rebuilds those two GLBs. Larger asset rebuilds and solver/topology changes remain
separate milestones so the inexpensive improvements can be judged first.

The tissue now provides contact against projected, deformed triangles, with a local spatial bin rebuilt after
motion. It still uses the original shared particle cage and coarse triangle removal; the continuous cut lips,
layer walls and independent layer response in package 6 are needed for the concept art's smooth incisions.
The visible hand and patient meshes also retain their current sculpted topology, so package 4 remains the
largest visual improvement still open.

## Acceptance gates

- Inspect the same deterministic screenshot views before/after in Compatibility and Forward+.
- Keep source material textures, channel selection, roughness and metallic response through import.
- Skin, gloves, metal and cloth remain distinguishable under surgical lighting, including all skin tones.
- Both hands keep finite, mirrored poses at straight/folded arm limits; inspect pencil, ring, fist and flat grips.
- Stitches only close openings when edges meet. Replicate the exact broken constraint, including crossing diagonals.
- Tissue contact agrees with its deformed visible surface. Preserve shallow/deep separation and sleep.
- Run the project's model, tissue, scenario and two-process network suite before committing.

## Later, higher-cost targets

Use a coarse simulation cage with bounded incision-local refinement; build cut lips and walls rather than
removing coarse triangles. Add independent bending, layer attachment/separation and compliant grip patches.
Prototype XPBD compliance before committing to it. Preserve stable topology IDs across peers.

Replace whole-organ squash with local, volume-preserving deformation only for manipulated organs. Select organs
by scenario rather than cycling generic models. Improve blood advection against the same deformed surface.
Keep larger fluid simulation and whole-body volumetric simulation outside the initial slice.

Build one polished abdominal operation first, then transfer the proven rules to the other scenarios.
