namespace Scalpel.UI;

/// <summary>
/// The X-ray film, full size on a lightbox, drawn from the shapes the cart captured. It develops slowly, from black,
/// over a few seconds. Metal glows white, bone is light gray, masses are soft gray, gas pockets are dark.
/// </summary>
public partial class XrayPhoto : Control
{
    private static readonly Vector2 PhotoSize = new(860f, 1000f);
    /// <summary>A portrait film sheet on the lightbox. Shape sizes below were tuned on a 540 px film and scale with it.
    /// </summary>
    private static readonly Rect2 Film = new(50f, 50f, 760f, 900f);
    private const float ShapeScale = 760f / 540f;
    private static readonly Color Black = new(0.02f, 0.025f, 0.035f);

    private readonly XrayCart _cart;
    private readonly XrayPrint _print;

    public XrayPhoto(XrayCart cart, XrayPrint print)
    {
        _cart = cart;
        _print = print;
        CustomMinimumSize = PhotoSize;
    }

    /// <summary>Redraws only while the print develops; once it's done the picture never changes.</summary>
    public override void _Process(double delta)
    {
        QueueRedraw();
        if (_cart.Developed() >= 1f)
        {
            SetProcess(false);
        }
    }

    public override void _Draw()
    {
        var develop = _cart.Developed();
        Color Fade(Color color) => Black.Lerp(color, develop);
        // Lightbox: a gray bezel around a cool white panel, the film clipped on top.
        DrawRect(new Rect2(Vector2.Zero, PhotoSize), new Color(0.2f, 0.21f, 0.22f));
        DrawRect(new Rect2(new Vector2(20f, 20f), PhotoSize - new Vector2(40f, 40f)), new Color(0.86f, 0.9f, 0.94f));
        DrawRect(Film, Black);
        DrawAnatomy(_print.Site, Fade);
        foreach (var shape in _print.Shapes)
        {
            DrawShape(shape, Fade);
        }
        var rng = new RandomNumberGenerator { Seed = _print.PrintedAtMsec };
        for (var i = 0; i < 900; i++)
        {
            var at = Film.Position + (new Vector2(rng.Randf(), rng.Randf()) * Film.Size);
            DrawRect(new Rect2(at, Vector2.One * 2f), new Color(1f, 1f, 1f, rng.Randf() * 0.07f * develop));
        }
        var font = GetThemeDefaultFont();
        var ink = Fade(new Color(0.9f, 0.92f, 0.95f));
        DrawString(font, Film.Position + new Vector2(24f, 52f), "L", HorizontalAlignment.Left, -1, 40, ink);
        var label = $"PORTABLE AP   {_print.Site.ToUpperInvariant().Replace('_', ' ')}";
        DrawString(font, Film.Position + new Vector2(24f, 44f), label, HorizontalAlignment.Right, Film.Size.X - 48f, 20, ink);
        // Film clips at the top of the lightbox.
        foreach (var x in (float[])[Film.Position.X + 120f, Film.End.X - 120f])
        {
            DrawRect(new Rect2(new Vector2(x - 20f, Film.Position.Y - 14f), new Vector2(40f, 22f)), new Color(0.55f, 0.57f, 0.6f));
        }
    }

    private static Vector2 OnFilm(Vector2 uv) => Film.Position + (uv.Clamp(Vector2.Zero, Vector2.One) * Film.Size);

    /// <summary>Faint body structures so the metal has context.</summary>
    private void DrawAnatomy(string site, Func<Color, Color> fade)
    {
        var bone = fade(new Color(0.42f, 0.44f, 0.46f));
        var soft = fade(new Color(0.14f, 0.16f, 0.17f));
        DrawRect(Film.Grow(-30f), soft);
        switch (site)
        {
            // Site u runs along the body (head to the right), v across it: the spine is horizontal, ribs cross it.
            case "chest" or "abdomen" or "back" or "shoulder":
                DrawRect(new Rect2(OnFilm(new Vector2(0f, 0.46f)), new Vector2(1f, 0.08f) * Film.Size), bone);
                if (site == "abdomen")
                {
                    DrawArc(OnFilm(new Vector2(-0.15f, 0.5f)), Film.Size.Y * 0.45f, -Mathf.Pi * 0.4f, Mathf.Pi * 0.4f, 32,
                        bone, 16f * ShapeScale);
                    break;
                }
                var radius = Film.Size.Y * 0.5f;
                for (var i = 0; i < 6; i++)
                {
                    var apex = OnFilm(new Vector2(0.18f + (i * 0.14f), 0.5f));
                    DrawArc(apex - new Vector2(radius, 0f), radius, -Mathf.Pi * 0.3f, Mathf.Pi * 0.3f, 24, bone, 7f * ShapeScale);
                }
                break;
            case "head" or "face":
                DrawCircle(OnFilm(new Vector2(0.5f, 0.5f)), Film.Size.X * 0.42f, bone);
                DrawCircle(OnFilm(new Vector2(0.5f, 0.5f)), Film.Size.X * 0.39f, soft);
                break;
            default:
                DrawRect(new Rect2(OnFilm(new Vector2(0f, 0.42f)), new Vector2(1f, 0.16f) * Film.Size), bone);
                break;
        }
    }

    private void DrawShape(XrayShape shape, Func<Color, Color> fade)
    {
        var at = OnFilm(shape.Uv);
        var metal = fade(new Color(0.97f, 0.97f, 0.95f));
        switch (shape.Kind)
        {
            case "bullet":
                DrawCircle(at, 9f * ShapeScale, metal);
                break;
            case "knife":
                DrawLine(at, at + (new Vector2(0f, 160f) * ShapeScale), metal, 10f * ShapeScale);
                break;
            case "figurine":
                DrawRect(new Rect2(at - (new Vector2(14f, 60f) * ShapeScale), new Vector2(28f, 120f) * ShapeScale), metal);
                break;
            case "tool":
                var direction = Vector2.Up.Rotated(shape.Yaw);
                DrawLine(at - (direction * 70f * ShapeScale), at + (direction * 70f * ShapeScale), metal, 7f * ShapeScale);
                break;
            case "rib":
                // A rib end runs from the break toward the side it came from (yaw 180 points the other way).
                var along = new Vector2(0f, Mathf.Abs(shape.Yaw) > Mathf.Pi * 0.5f ? -70f : 70f) * ShapeScale;
                DrawLine(at, at + along + new Vector2(-6f, 0f), fade(new Color(0.7f, 0.7f, 0.68f)), 9f * ShapeScale);
                break;
            case "splinter":
                DrawLine(at - (new Vector2(10f, 12f) * ShapeScale), at + (new Vector2(10f, 12f) * ShapeScale),
                    fade(new Color(0.75f, 0.75f, 0.72f)), 4f * ShapeScale);
                break;
            case "nasal_hump":
                // The bridge of the nose, running along the body, with the bump on top.
                var nose = fade(new Color(0.64f, 0.64f, 0.62f));
                DrawLine(at + (new Vector2(55f, 10f) * ShapeScale), at - (new Vector2(45f, 6f) * ShapeScale), nose, 10f * ShapeScale);
                DrawCircle(at + (new Vector2(0f, -2f) * ShapeScale), 9f * ShapeScale, nose);
                break;
            case "sternum":
                // The breastbone runs along the body: wide at the top (toward the head, +u), narrow at the tip.
                Vector2[] outline =
                [
                    new(110f, -26f), new(70f, -30f), new(-80f, -20f), new(-115f, 0f), new(-80f, 20f), new(70f, 30f),
                    new(110f, 26f),
                ];
                DrawColoredPolygon([.. outline.Select(point => at + (point * ShapeScale))], fade(new Color(0.66f, 0.66f, 0.64f)));
                break;
            case "skull_flap":
                // Seen from above: the outline of the flap the saw will cut, brighter at its edges.
                DrawRect(new Rect2(at - (new Vector2(95f, 80f) * ShapeScale), new Vector2(190f, 160f) * ShapeScale),
                    fade(new Color(0.6f, 0.6f, 0.58f)), false, 8f * ShapeScale);
                break;
            case "bone" or "fragment":
                DrawRect(new Rect2(at - (new Vector2(120f, 14f) * ShapeScale), new Vector2(240f, 28f) * ShapeScale),
                    fade(new Color(0.62f, 0.62f, 0.6f)));
                break;
            case "tumor" or "clot" or "appendix":
                DrawCircle(at, 22f * ShapeScale, fade(new Color(0.3f, 0.32f, 0.33f)));
                break;
            case "organ":
                DrawCircle(at, 55f * ShapeScale, fade(new Color(0.2f, 0.22f, 0.23f)));
                break;
            case "air":
                DrawCircle(at, 45f * ShapeScale, fade(new Color(0f, 0f, 0f)));
                break;
            case "fluid":
                DrawCircle(at, 50f * ShapeScale, fade(new Color(0.28f, 0.3f, 0.3f)));
                break;
        }
    }
}
