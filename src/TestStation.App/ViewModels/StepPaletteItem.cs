using TestStation.App.Localization;

namespace TestStation.App.ViewModels;

public sealed record StepPaletteItem(string Kind, string Category)
{
    public string Name => UiDisplay.StepType(Kind);
}

public sealed record StepPaletteGroup(string Name, IReadOnlyList<StepPaletteItem> Items);
