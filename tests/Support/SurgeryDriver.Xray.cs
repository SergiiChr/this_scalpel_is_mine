namespace Scalpel.Tests.Support;

/// <summary>The X-ray cart: pushing it to the table, taking films and holding them up.</summary>
public partial class SurgeryDriver {
    private XrayCart Cart => Surgery.Room.Xray!;

    private Interactable CartSpot(string prompt) =>
        Cart.FindChildren("*", "", true, false).OfType<Interactable>().First(spot => spot.Prompt.StartsWith(prompt));

    /// <summary>True when every part of <paramref name="spot"/>'s highlight is lit up.</summary>
    public static bool IsLit(Interactable spot) =>
        spot.Highlight.Count > 0 && spot.Highlight
            .SelectMany(part => part.FindChildren("*", "GeometryInstance3D", true, false).Append(part))
            .OfType<GeometryInstance3D>()
            .All(mesh => mesh.MaterialOverlay == Materials.Highlight);

    /// <summary>Looks at the cart's spot whose prompt starts with <paramref name="prompt"/> and presses interact.
    /// </summary>
    private async Task PlayerInteractsWithCart(string prompt) {
        await PlayerLooksAt(CartSpot(prompt).GlobalPosition);
        await Frames.Physics(2);
        PlayerInput.Tap(InputActions.Interact);
        await PlayerInput.Delivered();
        await Frames.Physics(2);
        Note($"interacts with the X-ray cart: {prompt}");
    }

    /// <summary>Takes hold of the cart by its handle and pushes it until the tube head hangs over
    /// <paramref name="point"/>, still holding it. With <paramref name="lateral"/>, the wheel first brings the head down
    /// until the lateral beam runs at that height.</summary>
    public async Task PlayerPushesXrayOver(Vector3 point, float? lateral = null) {
        var handle = Cart.ToGlobal(new Vector3(0f, 0.9f, 0.4f));
        await PlayerWalksTo(handle, 0.6f);
        await PlayerLooksAt(handle);
        await Frames.Physics(2);
        PlayerInput.Tap(InputActions.Grab);
        await PlayerInput.Delivered();
        await Frames.Physics(2);
        Note("takes hold of the X-ray cart");
        await Frames.Physics(30);
        // Out of its niche straight back first: turning inside it, it would catch on the niche's sides.
        var head = (Node3D)Cart.FindChild("Head", true, false);
        var wall = (Surgery.Room.Layout.Size.X * 0.5f) - 0.3f;
        for (var i = 0; i < 12 && Mathf.Abs(head.GlobalPosition.X) > wall; i++) {
            await PlayerHoldsWalkKey(InputActions.MoveBack, 0.25f, () => { });
        }
        await Frames.Physics(30);
        if (lateral is { } height) {
            await PlayerBringsLateralBeamTo(height);
        }
        // Held like a tool, the cart and its tube head keep where they are from the pusher, a little to one side or
        // turned if they were grabbed that way: the pusher stands as far from the point as the head is from them, and
        // faces off it by as much as the head is off their straight ahead.
        var reach = Me.ToLocal(head.GlobalPosition) with { Y = 0f };
        var held = Me.ToLocal(Cart.GlobalPosition) with { Y = 0f };
        var offAhead = Mathf.Atan2(-reach.X, -reach.Z);
        var turned = Cart.Rotation.Y - Me.Rotation.Y;
        var flat = point with { Y = Me.GlobalPosition.Y };
        // Across the table from the side the cart stands on first, then round toward its ends, until the pusher has
        // room to stand and the cart fits on the floor.
        var side = Mathf.Sign(Cart.GlobalPosition.Z - point.Z);
        Vector3? facing = null;
        foreach (var turn in (float[])[0f, 0.4f, -0.4f, 0.8f, -0.8f, Mathf.Pi, Mathf.Pi + 0.4f, Mathf.Pi - 0.4f]) {
            var away = new Vector3(0f, 0f, side).Rotated(Vector3.Up, turn);
            var stand = flat + (away * reach.Length());
            var ahead = (-away).Rotated(Vector3.Up, -offAhead);
            var yaw = Mathf.Atan2(-ahead.X, -ahead.Z);
            if (!Cart.Fits(stand + (new Basis(Vector3.Up, yaw) * held), yaw + turned)) {
                continue;
            }
            await PlayerWalksTo(stand, 0f);
            if ((Me.GlobalPosition with { Y = stand.Y }).DistanceTo(stand) < 0.02f) {
                facing = (stand + ahead) with { Y = point.Y };
                break;
            }
        }
        if (facing is not { } look) {
            throw new InvalidOperationException($"No room to push the X-ray cart over {point} in {Surgery.Scenario.Id}.");
        }
        await PlayerLooksAt(look);
        await Frames.Physics(60);
        Note($"pushes the X-ray cart over {point}");
    }

    /// <summary>Rolls the wheel a step at a time, watching the lateral field's outline, until the beam runs within a
    /// few centimeters of <paramref name="height"/>.</summary>
    private async Task PlayerBringsLateralBeamTo(float height) {
        for (var step = 0; step < 40; step++) {
            var off = Cart.Beams()[1].Source.Origin.Y - height;
            if (Mathf.Abs(off) < 0.03f) {
                break;
            }
            PlayerInput.Tap(off > 0f ? InputActions.LevelDown : InputActions.LevelUp);
            await PlayerInput.Delivered();
            await Frames.Physics(20);
        }
        Note($"brings the lateral beam to {Cart.Beams()[1].Source.Origin.Y:0.000} m for {height:0.000} m");
    }

    /// <summary>Takes an exposure with Use tool while holding the cart, lets go of it, locking it, and waits for both
    /// films to develop.</summary>
    public async Task PlayerTakesXray() {
        PlayerInput.Tap(InputActions.UseTool);
        await PlayerInput.Delivered();
        PlayerInput.Tap(InputActions.Grab);
        await PlayerInput.Delivered();
        Note("takes an X-ray and lets go of the cart");
        await Frames.Physics((int)((XrayCart.ExposeTime + XrayCart.DevelopTime) * Engine.PhysicsTicksPerSecond) + 10);
    }

    /// <summary>Takes the films out of the holder and holds them up in front of the eyes.</summary>
    public Task PlayerHoldsUpFilms() => PlayerInteractsWithCart("Hold up");
}
