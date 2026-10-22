namespace Scalpel.Tests.UI;

/// <summary>Scaled text preparation warms fallback glyphs before a fractional-oversampling draw occurs.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class FontPreparationTest {
    private const int OutlineSize = 2;
    private const float Oversampling = 1.5f;
    private const string Text = "\u2219";

    [TestCase(8, 12)]
    [TestCase(18, 27)]
    [TestCase(40, 60)]
    public void PreparedFallbackGlyphsDoNotGrowTheirCacheWhenDrawnAtFractionalPositions(int fontSize, int pixels) {
        using var primary = (FontFile)GD.Load<FontFile>("res://assets/fonts/LaBelleAurore-Regular.ttf").Duplicate();
        using var fallback = (FontFile)GD.Load<FontFile>("res://assets/fonts/IMFellEnglish-Regular.ttf").Duplicate();
        primary.ClearCache();
        fallback.ClearCache();
        primary.Fallbacks = [fallback];

        using var line = new TextLine();
        _ = line.AddString(Text, primary, fontSize);
        var server = TextServerManager.GetPrimaryInterface();
        var shaped = server.ShapedTextGetGlyphs(line.GetRid());
        var fallbackGlyphs = shaped
            .Select(glyph => (Font: glyph["font_rid"].AsRid(), Index: glyph["index"].AsInt64()))
            .Where(glyph => glyph.Font.IsValid && glyph.Index > 0).ToArray();
        AssertBool(fallbackGlyphs.Any(glyph => fallback.GetRids().Contains(glyph.Font)))
            .OverrideFailureMessage("the bullet operator is supplied by the fresh fallback font").IsTrue();
        var fontRids = fallbackGlyphs.Select(glyph => glyph.Font).Distinct().ToArray();

        Ui.PrepareFontText(primary, fontSize, OutlineSize, Text, Oversampling);
        var prepared = fontRids.Select(font => Snapshot(server, font)).ToArray();
        foreach (var cache in prepared) {
            AssertBool(cache.Sizes.Contains(new Vector2I(pixels, 0)))
                .OverrideFailureMessage("preparation caches fill glyphs at the viewport's raster size").IsTrue();
            AssertBool(cache.Sizes.Contains(new Vector2I(pixels, 3)))
                .OverrideFailureMessage("preparation caches the 2 px outline at 1.5 oversampling").IsTrue();
        }

        var canvas = RenderingServer.CanvasItemCreate();
        try {
            for (var shift = 0; shift < 4; shift++) {
                var at = new Vector2(shift * 0.25f, 0f);
                line.DrawOutline(canvas, at, OutlineSize, Colors.White, Oversampling);
                line.Draw(canvas, at, Colors.White, Oversampling);
            }
        } finally {
            RenderingServer.FreeRid(canvas);
        }

        foreach (var before in prepared) {
            var after = Snapshot(server, before.Font);
            AssertArray(after.Sizes).OverrideFailureMessage("the actual scaled draw adds no font size cache")
                .IsEqual(before.Sizes);
            foreach (var (size, glyphs) in before.Glyphs) {
                AssertArray(after.Glyphs[size])
                    .OverrideFailureMessage($"the actual scaled draw adds no glyphs at cache size {size}")
                    .IsEqual(glyphs);
            }
        }
    }

    private static CacheSnapshot Snapshot(TextServer server, Rid font) {
        var sizes = server.FontGetSizeCacheList(font).ToArray();
        var glyphs = sizes.ToDictionary(size => size, size => server.FontGetGlyphList(font, size).ToArray());
        return new CacheSnapshot(font, sizes, glyphs);
    }

    private sealed record CacheSnapshot(Rid Font, Vector2I[] Sizes, Dictionary<Vector2I, int[]> Glyphs);
}
