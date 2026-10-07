namespace Scalpel.Tests.Support;

/// <summary>Getting tools: off the tray, from the nurse, and back down again.</summary>
public partial class SurgeryDriver
{
    /// <summary>The free tools with this id that aren't used up, nearest the surgeon first.</summary>
    public List<SurgicalTool> FreeTools(string id) =>
    [
        .. Surgery.Tools.Tools.Values
            .Where(tool => tool.Def.Id == id && tool.State == ToolState.Free && tool.Charges != 0)
            .OrderBy(tool => tool.GlobalPosition.DistanceTo(Me.GlobalPosition)),
    ];

    /// <summary>Rings the nurse, puts <paramref name="ids"/> in the cart (repeats are more of the same) and places the
    /// order.</summary>
    public async Task PlayerOrders(IReadOnlyList<string> ids)
    {
        await PlayerFillsCart(ids);
        await PlayerPlacesOrder();
    }

    private NurseShop Shop => Surgery.Hud.Shop;

    /// <summary>Opens the nurse's shop and, for each of <paramref name="ids"/>, picks its category and presses its
    /// [+]. Presses past a full cart do nothing, as for a player.</summary>
    public async Task PlayerFillsCart(IReadOnlyList<string> ids)
    {
        Surgery.OpenNurse();
        await Frames.Physics(1);
        foreach (var id in ids)
        {
            PlayerPicksCategory(Db.Tool(id)!.Category);
            Click(Shop.RowOf(id).Add);
        }
        Note($"cart: {string.Join(", ", ids)}");
    }

    /// <summary>Clicks a button on screen, as a player can: nothing happens when it's disabled or hidden.</summary>
    public static void Click(BaseButton button)
    {
        if (!button.Disabled && button.IsVisibleInTree())
        {
            button.EmitSignal(BaseButton.SignalName.Pressed);
        }
    }

    /// <summary>Clicks a category in the open nurse's shop: its button stays down and its items show.</summary>
    public void PlayerPicksCategory(string category) => Shop.CategoryButtons[category].ButtonPressed = true;

    /// <summary>Presses [-] next to <paramref name="id"/> in the shown category of the open nurse's shop.</summary>
    public void PlayerRemovesFromCart(string id)
    {
        Click(Shop.RowOf(id).Remove);
        Note($"takes one {id} out of the cart");
    }

    /// <summary>Presses Place order once the nurse is free to take it (the button waits for her).</summary>
    public async Task PlayerPlacesOrder()
    {
        var place = Shop.PlaceButton;
        await Frames.Until(() => !place.Disabled, 60f);
        Click(place);
        await Frames.Physics(2);
        Note("places the order");
    }

    /// <summary>The tool in the active hand. Off the tray when it's there, otherwise ordered from the nurse and taken
    /// off the delivery tray. Null when there's none and nobody to fetch one.</summary>
    public async Task<SurgicalTool?> PlayerRequestsItem(string id)
    {
        if (Surgery.Finished)
        {
            return null;
        }
        if (Me.HeldTool(Me.Active) is { } held)
        {
            if (held.Def.Id == id && held.Charges != 0)
            {
                return held;
            }
            await PlayerPutsDown();
        }
        var found = FreeTools(id);
        if (found.Count == 0 && Surgery.Scenario.Nurse)
        {
            Note($"orders {id} from the nurse");
            await Frames.Until(() => Surgery.Nurse.Idle, 60f);
            await PlayerOrders([id]);
            await Frames.Until(() => FreeTools(id).Count > 0, 90f);
            await Frames.Seconds(1f);
            found = FreeTools(id);
        }
        if (found.Count == 0)
        {
            Note($"no {id} to be had");
            return null;
        }
        var tool = found[0];
        await PlayerWalksTo(tool.GlobalPosition);
        await PlayerReaches(tool.GlobalPosition);
        Tap(InputActions.Grab);
        await Frames.Physics(5);
        if (Me.HeldTool(Me.Active) != tool)
        {
            // Something else lay nearer the fingertips: take exactly this one, like reaching past the other.
            if (Me.HeldTool(Me.Active) is not null)
            {
                Tap(InputActions.Grab);
                await Frames.Physics(3);
            }
            Surgery.Tools.RequestGrab(tool, Me.Active);
            await Frames.Physics(3);
        }
        Note($"holds {id}");
        return Me.HeldTool(Me.Active);
    }

    /// <summary>Puts the active hand's tool back on the instrument tray (holding nothing, does nothing).
    /// <paramref name="standing"/>: a bottle is stood upright there (Grab held), so a needle can go in through its cap.
    /// </summary>
    public async Task PlayerPutsDown(bool standing = false)
    {
        if (Me.HeldTool(Me.Active) is not { } tool)
        {
            return;
        }
        Use(false);
        await Frames.Physics(3);
        if (tool.Hold is not null)
        {
            // A clamp still pinching: pressed again it lets go.
            Use();
            await Frames.Physics(3);
            Use(false);
            await Frames.Physics(3);
        }
        if (Me.UsesLevel(Me.Active))
        {
            await SetLevel(0);
        }
        var spot = SurgeryState.FreeTraySpot(Surgery);
        for (var i = 0; i < 4; i++)
        {
            await PlayerWalksTo(spot);
            // The middle of the tool over the spot: a bottle held in a fist lies well away from its tip.
            await PlayerReaches(spot - (tool.Middle() - tool.TipPosition()));
            await PlayerReaches(spot - (tool.Middle() - tool.TipPosition()));
            if ((tool.Middle() - spot).Slide(Vector3.Up).Length() < 0.04f)
            {
                break;
            }
            // Out of this tool's reach from the side of the tray: another spot.
            SurgeryState.TraySpotIsBad(Surgery, spot);
            spot = SurgeryState.FreeTraySpot(Surgery);
        }
        // Set down onto the tray before letting go, or a bottle would fall and roll.
        if (!ToolActions.TriggerNames.ContainsKey(tool.Def.Action))
        {
            Use();
            await Frames.Physics(15);
        }
        if (standing && tool.Def.Tray == "bottles")
        {
            Press(InputActions.Grab);
            await Frames.Seconds(Surgeon.StandHold + 0.2f);
            Release(InputActions.Grab);
        }
        else
        {
            Tap(InputActions.Grab);
        }
        Use(false);
        await Frames.Seconds(0.5f);
        if ((tool.Middle() - spot).Slide(Vector3.Up).Length() > 0.08f)
        {
            // It slid off whatever it was set down against: anything else set down there would too.
            SurgeryState.TraySpotIsBad(Surgery, spot);
        }
        Note($"puts {tool.Def.Id} down");
    }

    /// <summary>The tool lies on the instrument tray: its middle over the tray, on it or on what else lies there.
    /// </summary>
    public bool LiesOnTray(SurgicalTool tool)
    {
        var at = tool.Middle() - Surgery.Room.Layout.Tray;
        return tool.State == ToolState.Free && Mathf.Abs(at.X) < 0.36f && Mathf.Abs(at.Z) < 0.41f
            && Mathf.Abs(at.Y - Room.TraySurface) < 0.2f;
    }

    /// <summary>Holds Use tool on <paramref name="point"/> at effort <paramref name="level"/> for
    /// <paramref name="time"/> seconds.</summary>
    public async Task PlayerWorksAt(Vector3 point, int level, float time)
    {
        await PlayerReaches(point);
        await SetLevel(level);
        Use();
        await Frames.Seconds(time);
        Use(false);
        await Frames.Physics(3);
    }

    /// <summary>Like <see cref="PlayerWorksAt"/> along a path of points: lowered at the first, moved through the rest
    /// at <paramref name="speed"/>, lifted at the end.</summary>
    public async Task PlayerWorksAlong(IReadOnlyList<Vector3> points, int level, float speed = Slow)
    {
        await PlayerReaches(points[0]);
        await SetLevel(level);
        Use();
        await Frames.Seconds(0.3f);
        foreach (var point in points.Skip(1))
        {
            await PlayerSweepsTo(point, speed);
        }
        Use(false);
        await Frames.Physics(3);
    }

    /// <summary>Holds the bottle in the active hand tipped over <paramref name="dish"/> with Use tool for
    /// <paramref name="time"/> seconds, pouring.</summary>
    public async Task PlayerPoursInto(SurgicalTool dish, float time)
    {
        await PlayerWalksTo(dish.GlobalPosition);
        await PlayerWorksAt(dish.Middle() + (Vector3.Up * 0.05f), 0, time);
    }
}
