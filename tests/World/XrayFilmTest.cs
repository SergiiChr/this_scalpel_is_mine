namespace Scalpel.Tests.World;

/// <summary>X-ray films as a surgeon uses them to find what's in the patient: push the cart until the tube head hangs
/// over the site, lock it, expose, and hold the two films up. The AP film shows what's lodged under the site where it
/// lies: a bullet, the ends of a broken bone, a figurine, the appendix. With the head brought down until the lateral
/// beam runs through it, the lateral film shows it too, at its depth. Where the films are drawn (with key frames), metal
/// and bone come out white on them.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("visual_confirmation"), TestCategory("scenario")]
[GodotArgs("--fixed-fps", "60")]
[IsolateCases]
public class XrayFilmTest {
    // Temporary local budget override: recorded CPU overruns of the cloud release run remain measured.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 12f);

    /// <summary>How much brighter than the film around it something pronounced shows (0..1 of the picture).</summary>
    private const float Pronounced = 0.25f;
    /// <summary>How close (of the film) something shows to where it lies when checked: breathing moves the abdomen
    /// about a centimeter between the exposure and the check, which a lateral film shows.</summary>
    private const float ShownWithin = 0.05f;

    [TestCase("bullet_muscle", "bullet", 1, true, false)]
    [TestCase("open_fracture", "fragment", 2, true, false)]
    [TestCase("oscar_figurine", "figurine", 1, true, false)]
    [TestCase("appendectomy", "appendix", 1, false, false)]
    [TestCase("bullet_muscle", "bullet", 1, true, true, Description = "lateral")]
    [TestCase("oscar_figurine", "figurine", 1, true, true, Description = "lateral")]
    public async Task TheFilmsShowWhatIsLodgedUnderTheSite(string scenario, string kind, int count, bool pronounced,
        bool lateral) {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start(scenario);
            var cart = driver.Surgery.Room.Xray!;
            var targets = driver.Patient.Targets.Where(target => target.Kind == kind).ToList();
            AssertInt(targets.Count).OverrideFailureMessage($"{scenario} has {count} {kind}").IsEqual(count);
            var middle = targets.Aggregate(Vector3.Zero, (sum, target) => sum + target.GlobalPosition) / count;
            if (KeyFrames.Wanted()) {
                shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(driver.Surgery, $"xray_{scenario}{(lateral ? "_lateral" : "")}");
            }
            // Loading the scenario isn't gameplay: the frame budget counts from here.
            driver.Budget.Clear();
            await driver.PlayerParksXrayOver(middle, lateral ? middle.Y : null);
            var views = lateral ? cart.Beams() : cart.Beams()[..1];
            foreach (var beam in views) {
                AssertBool(targets.All(target => beam.FilmPoint(target.GlobalPosition) is not null))
                    .OverrideFailureMessage($"parked, the {beam.View} field takes in every {kind}").IsTrue();
            }
            if (shots is not null) {
                if (lateral) {
                    // Lowered just over the patient, the tube head fills a view from above.
                    await driver.PlayerLooksAt(middle);
                    await driver.Unbudgeted(() => shots.CaptureView("field",
                        "past the cart, the tube head lowered just over the patient, pale yellow light outlines on the "
                            + "body around the site"));
                } else {
                    // From the patient's feet: from above the tube head is in the way, from the cart's side its column.
                    await driver.Unbudgeted(() => shots.CaptureOblique("field",
                        "a pale yellow rectangle of light on the patient around the site, following the body's curves, "
                            + "the tube head above it",
                        middle, -driver.Patient.GlobalBasis.X, 0.8f));
                }
            }
            // With key frames, drawing is off for the frame budget, so the films are drawn by a forced draw of the
            // whole scene in software: not gameplay, where they're drawn within the frame. Headless it's all counted.
            await (KeyFrames.Wanted() ? driver.Unbudgeted(driver.PlayerTakesXray) : driver.PlayerTakesXray());
            AssertInt(cart.Films.Count).OverrideFailureMessage("an exposure gives two films").IsEqual(2);
            // Reading a film's picture back and scanning it is the test's work, not the game's.
            await driver.Unbudgeted(() => {
                foreach (var beam in views) {
                    ShowsWhereItLies(cart.Films.Single(film => film.Beam.View == beam.View), targets, kind, pronounced);
                }
                return Task.CompletedTask;
            });
            await driver.PlayerHoldsUpFilms();
            AssertBool(driver.Me.HoldingFilms).OverrideFailureMessage("interact on the holder holds the films up").IsTrue();
            if (shots is not null) {
                var lamp = driver.Surgery.Room.GetNode<Node3D>("SurgicalLamp").GlobalPosition;
                await driver.PlayerLooksAt(lamp);
                await driver.Unbudgeted(() => shots.CaptureView("films_held_up",
                    "two X-ray films held up against the surgical lamp, black where the beam got through, bones and "
                        + $"metal white; on the left one (AP) the {kind} where the site is"
                        + (lateral ? $", on the right one (lateral) the body from its side, the {kind} inside it" : "")));
            }
            PlayerInput.Tap(InputActions.Interact);
            await PlayerInput.Delivered();
            AssertBool(driver.Me.HoldingFilms).OverrideFailureMessage("interact puts the films back").IsFalse();
            driver.Budget.Check(localOverride: BudgetOverride);
        } finally {
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    /// <summary>The film shows every one of <paramref name="targets"/> where it lies, and where the film is drawn, a
    /// <paramref name="pronounced"/> one stands out white.</summary>
    private static void ShowsWhereItLies(XrayFilm film, List<CavityTarget> targets, string kind, bool pronounced) {
        var shown = film.Findings.Where(finding => finding.Kind == kind).ToList();
        AssertInt(shown.Count)
            .OverrideFailureMessage($"the {film.Beam.View} film shows every {kind}: {string.Join(", ", film.Findings)}")
            .IsEqual(targets.Count);
        foreach (var target in targets) {
            var expected = film.Beam.FilmPoint(target.GlobalPosition)!.Value;
            AssertBool(shown.Any(finding => finding.At.DistanceTo(expected) < ShownWithin))
                .OverrideFailureMessage($"on the {film.Beam.View} film the {kind} shows where it lies, {expected}: "
                    + string.Join(", ", shown))
                .IsTrue();
        }
        if (film.Image is not { } image || !pronounced) {
            return;
        }
        var picture = image.GetImage();
        foreach (var finding in shown) {
            var (spot, around) = Brightness(picture, finding.At);
            AssertFloat(spot - around)
                .OverrideFailureMessage($"the {kind} stands out white on the {film.Beam.View} film ({spot:0.00} against "
                    + $"{around:0.00})")
                .IsGreater(Pronounced);
        }
    }

    /// <summary>The picture's mean brightness on a small spot at <paramref name="at"/> (film 0..1), and on a ring
    /// around it.</summary>
    private static (float Spot, float Around) Brightness(Image picture, Vector2 at) {
        var size = picture.GetSize();
        var center = new Vector2I((int)(at.X * (size.X - 1)), (int)(at.Y * (size.Y - 1)));
        float Mean(int from, int to) {
            var (sum, n) = (0f, 0);
            for (var y = -to; y <= to; y++) {
                for (var x = -to; x <= to; x++) {
                    var p = center + new Vector2I(x, y);
                    var r = Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
                    if (r >= from && r <= to && p.X >= 0 && p.Y >= 0 && p.X < size.X && p.Y < size.Y) {
                        sum += picture.GetPixelv(p).R;
                        n++;
                    }
                }
            }
            return n == 0 ? 0f : sum / n;
        }
        return (Mean(0, 2), Mean(40, 44));
    }
}
