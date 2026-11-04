using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WordBase.Core.Models;

namespace WordBase.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppHost _host;

    public LibraryViewModel(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public ObservableCollection<EntrySummary> Items { get; } = new();

    public IReadOnlyList<string> LanguageFilters { get; } =
        new[] { "全部", "英语", "日语", "中文", "韩语", "德语", "法语", "西班牙语" };

    private static readonly string?[] FilterCodes = { null, "en", "ja", "zh", "ko", "de", "fr", "es" };

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private int languageIndex;
    [ObservableProperty] private string statsText = string.Empty;
    [ObservableProperty] private string emptyText = string.Empty;
    [ObservableProperty] private bool isEmpty;

    partial void OnLanguageIndexChanged(int value) => Refresh();

    partial void OnSearchTextChanged(string value) => Refresh();

    public string? CurrentLanguageCode =>
        LanguageIndex >= 0 && LanguageIndex < FilterCodes.Length ? FilterCodes[LanguageIndex] : null;

    public void Refresh()
    {
        var language = CurrentLanguageCode;
        Items.Clear();
        foreach (var item in _host.Vocabulary.Search(SearchText, language, 500))
        {
            Items.Add(item);
        }

        var stats = _host.Review.Stats();
        StatsText = $"共 {stats.Total} 条 · 今日到期 {stats.Due} · 新词 {stats.New} · 学习中 {stats.Learning} · 复习中 {stats.Review}";

        IsEmpty = Items.Count == 0;
        EmptyText = IsEmpty ? (string.IsNullOrWhiteSpace(SearchText) ? "词库还是空的，去「录入」页加第一个词吧。" : "没有匹配的词条。") : string.Empty;
    }

    public void Delete(long entryId)
    {
        _host.Entries.Delete(entryId);
        _host.Logger.Info($"删除词条 {entryId}");
        Refresh();
    }
}
