namespace Scalpel.Visuals;

/// <summary>The flat fabric ribbon around a limb, and the loose model's hardware in its mounted pose.</summary>
internal static class TourniquetVisual {
    internal const float Thickness = 0.003f;
    private const int Segments = 64;
    private static readonly string[] LooseParts = ["Strap", "Tail", "Keeper"];

    internal static ArrayMesh Band(float radius, float width) {
        const float Bevel = 0.00075f;
        Vector2[] profile = [
            new(radius, width / 2f - Bevel), new(radius, -width / 2f + Bevel),
            new(radius + Bevel, -width / 2f), new(radius + Thickness - Bevel, -width / 2f),
            new(radius + Thickness, -width / 2f + Bevel), new(radius + Thickness, width / 2f - Bevel),
            new(radius + Thickness - Bevel, width / 2f), new(radius + Bevel, width / 2f),
        ];
        using var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var segment = 0; segment < Segments; segment++) {
            var angle = Mathf.Tau * segment / Segments;
            var nextAngle = Mathf.Tau * (segment + 1) / Segments;
            for (var edge = 0; edge < profile.Length; edge++) {
                var a = profile[edge];
                var b = profile[(edge + 1) % profile.Length];
                var normal = new Vector2(b.Y - a.Y, a.X - b.X).Normalized();
                Vertex(angle, a, normal);
                Vertex(angle, b, normal);
                Vertex(nextAngle, a, normal);
                Vertex(nextAngle, a, normal);
                Vertex(angle, b, normal);
                Vertex(nextAngle, b, normal);
            }
        }
        return surface.Commit();

        void Vertex(float angle, Vector2 point, Vector2 normal) {
            var radial = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            surface.SetNormal(radial * normal.X + Vector3.Up * normal.Y);
            surface.AddVertex(radial * point.X + Vector3.Up * point.Y);
        }
    }

    internal static void SetWrapped(Node3D model, float length, bool wrapped) {
        // The hardware's mounting center is shared with the procedural model generator.
        model.Position = wrapped ? new Vector3(0f, 0f, length * 0.7f) : Vector3.Zero;
        foreach (var name in LooseParts) {
            if (model.FindChild(name, true, false) is Node3D part) {
                part.Visible = !wrapped;
            }
        }
    }
}
