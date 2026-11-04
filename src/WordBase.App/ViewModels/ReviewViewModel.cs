using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WordBase.Core.Models;

namespace WordBase.App.ViewModels;

public sealed partial class ReviewViewModel : ObservableObject
{
    private readonly AppHost _host;

    public ReviewViewModel(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public ObservableCollection<EntrySummary> DueItems { get; } = new();

    [ObservableProperty] private string statsText = string.Empty;
    [ObservableProperty] private string message = string.Empty;
    [ObservableProperty] private bool hasDue;

    public void Refresh()
    {
        var stats = _host.Review.Stats();
        StatsText = $"总词条 {stats.Total}\n今日到期 {stats.Due}\n新词 {stats.New} · 学习 {stats.Learning} · 复习 {stats.Review} · 重学 {stats.Relearning}";

        // 列表里展示全部到期条目，但复习会话会按每日上限截断
        var due = _host.Entries.GetDue(DateTimeOffset.Now, Math.Max(stats.Due, 1));
        DueItems.Clear();
        foreach (var entry in due)
        {
            DueItems.Add(new EntrySummary(entry.Id, entry.Language, entry.Kind, entry.Term, entry.ReadingsDisplay,
                entry.PrimaryGloss, entry.Examples.Count, entry.Review.DueAt, entry.Review.State));
        }

        HasDue = DueItems.Count > 0;
        Message = HasDue
            ? $"有 {DueItems.Count} 个词到期待复习。"
            : "现在没有到期的词条。到点后会在后台用 Windows 通知提醒你。";
    }
}
