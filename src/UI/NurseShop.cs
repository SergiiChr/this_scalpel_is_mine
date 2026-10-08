namespace Scalpel.UI;

/// <summary>
/// The nurse bell as a shop: categories on the left, the chosen one's items as a list with [-] count [+], and the cart
/// on the right. A cart holds up to <see cref="Nurse.Batch"/> items, the same one more than once too, sent as one order.
/// Built once per surgery and only shown and hidden after that, so ringing the bell doesn't build a page of controls
/// mid-surgery. The cart stays between visits until it's ordered or emptied. Place order waits for the nurse to be free.
/// </summary>
public sealed class NurseShop
{
    /// <summary>The whole overlay, for the HUD to show and hide.</summary>
    public Control View { get; }
    public Button PlaceButton { get; }
    /// <summary>Category name -> the button that shows its list.</summary>
    public IReadOnlyDictionary<string, Button> CategoryButtons => _categories;
    private readonly Dictionary<string, Button> _categories = [];
    private readonly Dictionary<string, ItemRow> _rows = [];
    private readonly List<string> _cart = [];
    private readonly Action<IReadOnlyList<string>> _order;
    private readonly Label _subtitle;
    private readonly Label _cartTitle;
    private readonly Label _cartLines;
    private readonly Label _cartEta;
    private bool _nurseReady = true;

    /// <summary>One orderable tool's row: how many are in the cart, and the buttons that change it.</summary>
    public sealed record ItemRow(Label Count, Button Remove, Button Add);

    /// <summary><paramref name="groups"/>: category -> tools, in display order. <paramref name="order"/> receives the
    /// cart's tool ids, in the order added.</summary>
    public NurseShop(
        IEnumerable<(string Category, IReadOnlyList<ToolDef> Tools)> groups, Action<IReadOnlyList<string>> order,
        Action close)
    {
        _order = order;
        var categories = Ui.VBox(4);
        categories.CustomMinimumSize = new Vector2(240f, 0f);
        var picked = new ButtonGroup();
        var lists = Ui.VBox(0);
        foreach (var (category, tools) in groups)
        {
            var list = Ui.VBox(4);
            list.Hide();
            foreach (var def in tools)
            {
                list.AddChild(Row(def));
            }
            lists.AddChild(list);
            var button = new Button
            {
                Text = category,
                ToggleMode = true,
                ButtonGroup = picked,
                Alignment = HorizontalAlignment.Left,
            };
            button.Toggled += on => list.Visible = on;
            categories.AddChild(button);
            _categories[category] = button;
        }
        if (_categories.Count > 0)
        {
            _categories.Values.First().ButtonPressed = true;
        }
        _cartTitle = Ui.Label("", 22, Ui.Pip);
        _cartLines = Ui.Label("", 18);
        _cartLines.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _cartEta = Ui.Label("", 16, Ui.Dim);
        PlaceButton = Ui.Button("Place order", Order);
        var cart = Ui.VBox(8);
        cart.CustomMinimumSize = new Vector2(300f, 0f);
        foreach (var part in (Control[])[_cartTitle, _cartLines, _cartEta, PlaceButton, Ui.Button("Empty cart", Clear)])
        {
            cart.AddChild(part);
        }
        var columns = Ui.HBox(16);
        columns.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        columns.AddChild(Ui.Scroll(categories));
        var items = Ui.Scroll(lists);
        items.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        columns.AddChild(items);
        columns.AddChild(Ui.PanelAround(cart));
        var page = Ui.VBox(6);
        page.AddChild(Ui.Label("Ring for the nurse", 28, Ui.Pip));
        _subtitle = Ui.Label("", 16, Ui.Dim, true);
        page.AddChild(_subtitle);
        page.AddChild(columns);
        page.AddChild(Ui.Button("Never mind  [Esc]", close));
        View = Ui.CenteredPanel(page, new Vector2(1100f, 640f), new Color(0f, 0f, 0f, 0.6f));
        Refresh();
    }

    /// <summary>A tool's row in its category's list.</summary>
    public ItemRow RowOf(string toolId) => _rows[toolId];

    /// <summary>While it's shown: <paramref name="board"/> is the nurse board's text, <paramref name="nurseReady"/>
    /// whether she takes an order now.</summary>
    public void Update(string board, bool nurseReady)
    {
        _subtitle.Text = board;
        if (nurseReady != _nurseReady)
        {
            _nurseReady = nurseReady;
            Refresh();
        }
    }

    public void Clear()
    {
        _cart.Clear();
        Refresh();
    }

    private HBoxContainer Row(ToolDef def)
    {
        var row = Ui.HBox(8);
        var name = Ui.Label(def.Name, 20);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(name);
        row.AddChild(Ui.Label($"{(int)def.Delay} s", 18, Ui.Dim));
        var remove = Ui.Button(" − ", () => Change(def.Id, false));
        var count = Ui.Label("", 20);
        count.CustomMinimumSize = new Vector2(32f, 0f);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        var add = Ui.Button(" + ", () => Change(def.Id, true));
        foreach (var part in (Control[])[remove, count, add])
        {
            row.AddChild(part);
        }
        _rows[def.Id] = new ItemRow(count, remove, add);
        return row;
    }

    private void Change(string id, bool add)
    {
        if (add && _cart.Count < Nurse.Batch)
        {
            _cart.Add(id);
        }
        else if (!add && _cart.LastIndexOf(id) is var last and >= 0)
        {
            _cart.RemoveAt(last);
        }
        Refresh();
    }

    private void Order()
    {
        _order([.. _cart]);
        Clear();
    }

    private void Refresh()
    {
        var full = _cart.Count >= Nurse.Batch;
        foreach (var (id, row) in _rows)
        {
            var count = _cart.Count(item => item == id);
            row.Count.Text = count.ToString();
            row.Remove.Disabled = count == 0;
            row.Add.Disabled = full;
        }
        var lines = _cart.Distinct().Select(id => $"{Db.Tool(id)!.Name}  ×{_cart.Count(item => item == id)}").ToList();
        var longest = _cart.Select(id => Db.Tool(id)!.Delay).DefaultIfEmpty(0f).Max();
        _cartLines.Text = lines.Count > 0 ? string.Join("\n", lines) : "Nothing yet.";
        _cartTitle.Text = $"Cart  {_cart.Count} / {Nurse.Batch}";
        _cartEta.Text = longest > 0f ? $"Ready in about {(int)longest} s" : "";
        PlaceButton.Disabled = _cart.Count == 0 || !_nurseReady;
    }
}
