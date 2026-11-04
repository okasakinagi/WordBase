using CommunityToolkit.Mvvm.ComponentModel;

namespace WordBase.App.ViewModels;

public sealed record ReadingOption(long Id, string Display)
{
    public override string ToString() => Display;
}

public sealed partial class ReadingRowViewModel : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private bool isCommon = true;
    [ObservableProperty] private string? note;
}

public sealed partial class SenseRowViewModel : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty] private string gloss = string.Empty;
    [ObservableProperty] private bool isVerified = true;
    [ObservableProperty] private ReadingOption? selectedReading;

    public System.Collections.ObjectModel.ObservableCollection<ReadingOption> ReadingOptions { get; } = new();
}

public sealed partial class ExampleRowViewModel : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private string? translation;
    [ObservableProperty] private string? partOfSpeech;
    [ObservableProperty] private bool isVerified = true;
    [ObservableProperty] private ReadingOption? selectedReading;

    public System.Collections.ObjectModel.ObservableCollection<ReadingOption> ReadingOptions { get; } = new();
}
