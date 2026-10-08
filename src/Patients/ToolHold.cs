namespace Scalpel.Patients;

/// <summary>
/// What a tool has hold of in the patient (host only): made by <see cref="Patient.Grip"/> or
/// <see cref="Patient.SetSpreader"/>, moved by <see cref="Patient.UpdateGrip"/> and let go by
/// <see cref="Patient.ReleaseGrip"/>. A tool holding nothing has none (null).
/// </summary>
public abstract record ToolHold;

/// <summary>Skin pinched or hooked at <paramref name="Anchor"/> (uv), beside <paramref name="Wound"/> (0 for none).
/// A hook (the retractor) pulls the skin aside or up, never down into the opening. A piece of skin cut out all round
/// comes off when lifted. <paramref name="Hold"/>: where a self-retaining tool set down keeps it (site space).</summary>
public sealed record SkinHold(int Wound, Vector2 Anchor, bool Hook, bool Piece = false, Vector3? Hold = null) : ToolHold;

/// <summary>A cavity target (by index) taken hold of while still attached, at <paramref name="StartDepth"/>.</summary>
public sealed record TargetHold(int Target, float StartDepth) : ToolHold;

/// <summary>A cavity target (by index) taken out and carried in the jaws.</summary>
public sealed record CarryHold(int Target) : ToolHold;

/// <summary>A bleeding vessel (internal wound by id) clamped shut.</summary>
public sealed record VesselHold(int Wound) : ToolHold;

/// <summary>An organ (by index) held aside, the tip <paramref name="Offset"/> (site space) from where it holds it.
/// </summary>
public sealed record OrganHold(int Organ, Vector3 Offset) : ToolHold;

/// <summary>
/// A spreader (the Gelpi retractor) set in a cut: its jaws' tissue grip keys, where each held its edge, the axis they
/// open along (site space), how far apart its tips were when set, the cut's middle (uv) and how deep the cut between
/// the tips goes (meters).
/// </summary>
public sealed record SpreadHold(int[] Keys, Vector3[] Starts, Vector3 Axis, float Spread, Vector2 Middle, float Depth) : ToolHold;
