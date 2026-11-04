using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using WordBase.Core.Models;
using WordBase.Core.Scheduling;

namespace WordBase.App.ViewModels;

public sealed record QuizOptionViewModel(string Text, bool IsCorrect, long? EntryId);

/// <summary>
/// 一次复习会话：认识/不认识 → 概率抽测选择题 → 揭示答案 → 自评。
/// 所有结算都走 ReviewService，保证 FSRS 状态与日志只有一个写入口。
/// </summary>
public sealed partial class ReviewSessionViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Random _rng = new();
    private readonly List<Entry> _queue = new();

    private int _index;
    private Entry? _current;
    private QuizQuestionState? _quiz;
    private DateTimeOffset _shownAt;

    public ReviewSessionViewModel(AppHost host) => _host = host;

    public event Action? SessionCompleted;

    public ObservableCollection<QuizOptionViewModel> QuizOptions { get; } = new();
    public ObservableCollection<string> AnswerLines { get; } = new();
    public ObservableCollection<string> ExampleLines { get; } = new();

    [ObservableProperty] private string prompt = string.Empty;
    [ObservableProperty] private string reading = string.Empty;
    [ObservableProperty] private string progressText = string.Empty;
    [ObservableProperty] private string feedback = string.Empty;
    [ObservableProperty] private string answerTitle = "释义";
    [ObservableProperty] private string summary = string.Empty;

    [ObservableProperty] private Visibility choiceVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility quizVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility revealVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility doneVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility feedbackVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility ratingVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility continueVisibility = Visibility.Collapsed;

    public int ReviewedCount { get; private set; }

    /// <summary>当前这张之后还剩多少（不含当前）。</summary>
    public int RemainingCount => Math.Max(0, _queue.Count - _index - 1);

    public void Start(long? entryId = null, bool? presetKnown = null)
    {
        _queue.Clear();
        _index = 0;
        ReviewedCount = 0;

        if (entryId is { } id)
        {
            var entry = _host.Entries.Get(id);
            if (entry is not null)
            {
                _queue.Add(entry);
            }
        }
        else
        {
            var limit = Math.Max(1, _host.Settings.DailyLimit);
            _queue.AddRange(_host.Review.GetDue(limit));
        }

        if (_queue.Count == 0)
        {
            ShowDone("现在没有到期的词条。");
            return;
        }

        ShowCurrent();

        if (presetKnown is true)
        {
            ChooseKnown();
        }
        else if (presetKnown is false)
        {
            ChooseUnknown();
        }
    }

    private void ShowCurrent()
    {
        if (_index >= _queue.Count)
        {
            ShowDone("本轮到期的词都复习完了。");
            return;
        }

        _current = _host.Entries.Get(_queue[_index].Id) ?? _queue[_index];
        _shownAt = DateTimeOffset.Now;

        Prompt = _current.Term;
        Reading = _current.ReadingsDisplay;
        ProgressText = $"第 {_index + 1} / {_queue.Count} 个 · 剩余 {RemainingCount}";

        SetStage(Stage.Choice);
        Summary = string.Empty;
    }

    public void ChooseKnown()
    {
        if (_current is null)
        {
            return;
        }

        var shouldQuiz = _host.Settings.QuizProbability > _rng.NextDouble();
        _quiz = shouldQuiz ? BuildQuiz(_current) : null;

        if (_quiz is null)
        {
            Submit(Interaction.KnownNotQuizzed);
            Advance("认识，已排到下一次复习。");
            return;
        }

        QuizOptions.Clear();
        foreach (var option in _quiz.Options)
        {
            QuizOptions.Add(new QuizOptionViewModel(option.Text, option.IsCorrect, option.SourceEntryId));
        }

        SetStage(Stage.Quiz);
    }

    public void ChooseUnknown()
    {
        if (_current is null)
        {
            return;
        }

        Reveal(showRating: true, feedbackText: null);
    }

    public void SelectOption(QuizOptionViewModel option)
    {
        if (_current is null)
        {
            return;
        }

        if (option.IsCorrect)
        {
            Submit(Interaction.KnownAndCorrect);
            Advance("答对了。");
            return;
        }

        var correct = _current.PrimaryGloss;
        Submit(Interaction.KnownButWrong, confusedWith: option.EntryId);
        Reveal(showRating: false, feedbackText: $"答错了。正确答案：{correct}");
    }

    public void Rate(Rating rating)
    {
        Submit(Interaction.NotKnown, selfRating: rating);
        Advance(rating == Rating.Again ? "已加入重学。" : "已记录。");
    }

    public void Continue() => Advance(null);

    private void Submit(Interaction interaction, Rating? selfRating = null, long? confusedWith = null)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            var responseMs = (int)Math.Max(0, (DateTimeOffset.Now - _shownAt).TotalMilliseconds);
            _host.Review.Submit(_current, interaction, selfRating, responseMs, confusedWith);
            ReviewedCount++;
        }
        catch (Exception ex)
        {
            _host.Logger.Error("提交复习结果失败", ex);
        }
    }

    private void Advance(string? feedbackText)
    {
        _index++;
        if (feedbackText is not null)
        {
            Feedback = feedbackText;
        }

        if (_index >= _queue.Count)
        {
            ShowDone(feedbackText ?? "本轮完成。");
            return;
        }

        ShowCurrent();
        if (!string.IsNullOrEmpty(feedbackText))
        {
            Feedback = feedbackText;
            FeedbackVisibility = Visibility.Visible;
        }
    }

    private void Reveal(bool showRating, string? feedbackText)
    {
        if (_current is null)
        {
            return;
        }

        AnswerLines.Clear();
        foreach (var sense in _current.Senses.Where(s => !string.IsNullOrWhiteSpace(s.Gloss)))
        {
            var reading = _current.Readings.FirstOrDefault(r => r.Id == sense.ReadingId)?.Text;
            var verifiedMark = sense.IsVerified ? string.Empty : "（未校对）";
            AnswerLines.Add(string.IsNullOrEmpty(reading) ? $"{sense.Gloss}{verifiedMark}" : $"{reading} · {sense.Gloss}{verifiedMark}");
        }

        if (AnswerLines.Count == 0)
        {
            AnswerLines.Add("（这个词条还没有释义）");
        }

        ExampleLines.Clear();
        foreach (var example in _current.Examples.Where(e => !string.IsNullOrWhiteSpace(e.Text)).Take(4))
        {
            var pos = string.IsNullOrWhiteSpace(example.PartOfSpeech) ? string.Empty : $"[{example.PartOfSpeech}] ";
            var translation = string.IsNullOrWhiteSpace(example.Translation) ? string.Empty : $"\n    {example.Translation}";
            ExampleLines.Add($"{pos}{example.Text}{translation}");
        }

        if (ExampleLines.Count == 0)
        {
            ExampleLines.Add("（这个词条还没有例句）");
        }

        SetStage(Stage.Reveal);
        RatingVisibility = showRating ? Visibility.Visible : Visibility.Collapsed;
        ContinueVisibility = showRating ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrEmpty(feedbackText))
        {
            Feedback = feedbackText;
            FeedbackVisibility = Visibility.Visible;
            AnswerTitle = "正确释义";
        }
        else
        {
            FeedbackVisibility = Visibility.Collapsed;
            AnswerTitle = "释义与例句";
        }
    }

    private void ShowDone(string text)
    {
        SetStage(Stage.Done);
        Summary = $"{text}（本次复习 {ReviewedCount} 个）";
        Prompt = "完成";
        Reading = string.Empty;
        ProgressText = string.Empty;
        SessionCompleted?.Invoke();
    }

    private QuizQuestionState? BuildQuiz(Entry entry)
    {
        var question = _host.Review.BuildQuiz(entry, 4, _rng);
        if (question is null)
        {
            return null;
        }

        return new QuizQuestionState(question.Options.ToList());
    }

    private void SetStage(Stage stage)
    {
        ChoiceVisibility = stage == Stage.Choice ? Visibility.Visible : Visibility.Collapsed;
        QuizVisibility = stage == Stage.Quiz ? Visibility.Visible : Visibility.Collapsed;
        RevealVisibility = stage == Stage.Reveal ? Visibility.Visible : Visibility.Collapsed;
        DoneVisibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;

        if (stage != Stage.Reveal)
        {
            RatingVisibility = Visibility.Collapsed;
            ContinueVisibility = Visibility.Collapsed;
        }

        if (stage == Stage.Choice || stage == Stage.Quiz)
        {
            FeedbackVisibility = Visibility.Collapsed;
        }
    }

    private enum Stage
    {
        Choice,
        Quiz,
        Reveal,
        Done,
    }

    private sealed record QuizQuestionState(IReadOnlyList<WordBase.Core.Quizzing.QuizOption> Options);
}
