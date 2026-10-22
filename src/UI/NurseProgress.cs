namespace Scalpel.UI;

/// <summary>The nurse's ten progress blocks, drawn without font fallback or per-block layout work.</summary>
internal sealed partial class NurseProgress : Control {
    internal const int Steps = 10;
    private const float Block = 12f;
    private const float Gap = 2f;

    internal int Filled { get; private set; }

    public NurseProgress() {
        CustomMinimumSize = new Vector2((Steps * (Block + Gap)) - Gap, Block);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    internal void ShowProgress(float progress) {
        var filled = Mathf.Clamp(Mathf.RoundToInt(progress * Steps), 0, Steps);
        if (filled != Filled) {
            Filled = filled;
            QueueRedraw();
        }
    }

    public override void _Draw() {
        for (var i = 0; i < Steps; i++) {
            var rect = new Rect2(i * (Block + Gap), 0f, Block, Block);
            DrawRect(i < Filled ? rect : rect.Grow(-0.5f), Ui.Dim, i < Filled);
        }
    }
}
