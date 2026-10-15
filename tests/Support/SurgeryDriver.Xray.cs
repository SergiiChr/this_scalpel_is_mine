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

    /// <summary>Takes the cart by its handle and pushes it until the tube head hangs over <paramref name="point"/>,
    /// then locks it against the floor. With <paramref name="lateral"/>, the wheel first brings the head down until the
    /// lateral beam runs at that height.</summary>
    public async Task PlayerParksXrayOver(Vector3 point, float? lateral = null) {
        var handle = CartSpot("Push").GlobalPosition;
        await PlayerWalksTo(handle, 0.6f);
        await PlayerInteractsWithCart("Push");
        if (lateral is { } height) {
            await PlayerBringsLateralBeamTo(height);
        }
        // The head reaches this far ahead of the cart, and the cart is this far ahead of its pusher.
        var reach = -Cart.ToLocal(((Node3D)Cart.FindChild("Head", true, false)).GlobalPosition).Z;
        var flat = point with { Y = Me.GlobalPosition.Y };
        // Across the table from the side the cart stands on first, then round toward its ends, until the pusher has
        // room to stand and the cart fits on the floor.
        var side = Mathf.Sign(Cart.GlobalPosition.Z - point.Z);
        var parked = false;
        foreach (var turn in (float[])[0f, 0.4f, -0.4f, 0.8f, -0.8f, Mathf.Pi, Mathf.Pi + 0.4f, Mathf.Pi - 0.4f]) {
            var away = new Vector3(0f, 0f, side).Rotated(Vector3.Up, turn);
            var cart = flat + (away * reach);
            if (Mathf.Abs(cart.X) > Cart.Bounds.X || Mathf.Abs(cart.Z) > Cart.Bounds.Y) {
                continue;
            }
            var stand = flat + (away * (reach + XrayCart.PushOffset));
            await PlayerWalksTo(stand, 0f);
            if ((Me.GlobalPosition with { Y = stand.Y }).DistanceTo(stand) < 0.02f) {
                parked = true;
                break;
            }
        }
        if (!parked) {
            throw new InvalidOperationException($"No room to push the X-ray cart over {point} in {Surgery.Scenario.Id}.");
        }
        await PlayerLooksAt(point);
        await Frames.Physics(60);
        PlayerInput.Tap(InputActions.UseTool);
        await PlayerInput.Delivered();
        Note($"parks the X-ray cart over {point}");
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

    /// <summary>Takes an exposure from the tube head and waits for both films to develop.</summary>
    public async Task PlayerTakesXray() {
        await PlayerInteractsWithCart("Take an X-ray");
        await Frames.Physics((int)((XrayCart.ExposeTime + XrayCart.DevelopTime) * Engine.PhysicsTicksPerSecond) + 10);
    }

    /// <summary>Takes the films out of the holder and holds them up in front of the eyes.</summary>
    public Task PlayerHoldsUpFilms() => PlayerInteractsWithCart("Hold up");
}
