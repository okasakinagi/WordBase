using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using WordBase.Core.Models;
using WordBase.Core.Services;
using WordBase.Core.Validation;
using WordBase.Llm;

namespace WordBase.App.ViewModels;

/// <summary>
/// 录入页的核心逻辑：查重 → 三条录入路径 → 可编辑确认表单 → 保存。
/// 三个入口（大模型 / 粘贴 / 手填）最终都落到同一份行集合上，保存前必须人工确认。
/// </summary>
public sealed partial class EntryEditorViewModel : ObservableObject
{
    private readonly AppHost _host;
    private long _editingId;
    private long _duplicateId;
    private long _nextTempReadingId = -1;
    private long _nextTempSenseId = -1;
    private Entry? _pendingReplacement;

    public EntryEditorViewModel(AppHost host)
    {
        _host = host;
        Language = host.Settings.DefaultLanguage;
        var index = Languages.ToList().FindIndex(l => l == Language);
        LanguageIndex = index < 0 ? 0 : index;
        ResetDraft(string.Empty);
    }

    public ObservableCollection<ReadingRowViewModel> Readings { get; } = new();
    public ObservableCollection<SenseRowViewModel> Senses { get; } = new();
    public ObservableCollection<ExampleRowViewModel> Examples { get; } = new();

    public IReadOnlyList<string> Languages { get; } =
        new[] { "en", "ja", "zh", "ko", "de", "fr", "es" };

    public IReadOnlyList<string> LanguageDisplays { get; } =
        new[] { "英语", "日语", "中文", "韩语", "德语", "法语", "西班牙语" };

    public IReadOnlyList<string> Kinds { get; } = new[] { "单词", "语法" };

    [ObservableProperty] private string term = string.Empty;
    [ObservableProperty] private string language = "en";
    [ObservableProperty] private int languageIndex;
    [ObservableProperty] private int kindIndex;
    [ObservableProperty] private string statusMessage = "输入词条后点「查询」，没有记录就可以开始录入。";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string validationSummary = string.Empty;
    [ObservableProperty] private string coverageSummary = string.Empty;
    [ObservableProperty] private string pasteText = string.Empty;
    [ObservableProperty] private string duplicateHeader = string.Empty;
    [ObservableProperty] private string duplicateDetail = string.Empty;
    [ObservableProperty] private string duplicateExamples = string.Empty;
    [ObservableProperty] private string editorHeader = "新建词条";
    [ObservableProperty] private Visibility duplicateVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility editorVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility busyVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility topUpVisibility = Visibility.Collapsed;
    [ObservableProperty] private string llmButtonText = "用大模型填充";

    public EntryKind CurrentKind => KindIndex == 1 ? EntryKind.Grammar : EntryKind.Word;

    partial void OnLanguageIndexChanged(int value)
    {
        if (value >= 0 && value < Languages.Count)
        {
            Language = Languages[value];
            _host.Settings.DefaultLanguage = Language;
        }
    }

    partial void OnIsBusyChanged(bool value) =>
        BusyVisibility = value ? Visibility.Visible : Visibility.Collapsed;

    public void Search()
    {
        var query = Core.Text.TermNormalizer.Tidy(Term);
        if (query.Length == 0)
        {
            StatusMessage = "请先输入词条。";
            return;
        }

        Term = query;
        var found = _host.Vocabulary.Lookup(Language, query);
        if (found is null)
        {
            ResetDraft(query);
            StatusMessage = $"没有找到「{query}」，可以开始录入了。";
        }
        else
        {
            // 查词即「遇见」：记录一次，并把该词的复习时间小幅提前
            _host.Vocabulary.RecordEncounter(found.Id, DuplicateChannel.Lookup, query);
            var refreshed = _host.Vocabulary.Lookup(Language, query) ?? found;
            ShowDuplicate(refreshed, isFreshLookup: true);
            StatusMessage = "这个词已经在库里了。已为你记录一次「遇见」，会适当加强它的复习。";
        }
    }

    public void ShowDuplicate(Entry entry, bool isFreshLookup)
    {
        _duplicateId = entry.Id;
        _editingId = 0;
        DuplicateHeader = $"{entry.Term}  {(entry.ReadingsDisplay.Length > 0 ? $"（{entry.ReadingsDisplay}）" : string.Empty)}  ·  " +
                          $"遇见 {entry.HitCount} 次";

        var lines = new List<string>();
        foreach (var sense in entry.Senses.Where(s => !string.IsNullOrWhiteSpace(s.Gloss)))
        {
            lines.Add($"· {sense.Gloss}");
        }

        foreach (var group in entry.Examples.Where(e => !string.IsNullOrWhiteSpace(e.Text)).GroupBy(e => e.PartOfSpeech ?? "未标词性"))
        {
            lines.Add($"【{group.Key}】");
            foreach (var example in group.Take(3))
            {
                lines.Add($"  {example.Text}");
                if (!string.IsNullOrWhiteSpace(example.Translation))
                {
                    lines.Add($"  {example.Translation}");
                }
            }
        }

        DuplicateDetail = lines.Count == 0 ? "（这个词条还没有释义/例句）" : string.Join(Environment.NewLine, lines);
        DuplicateExamples = $"复习状态：{entry.Review.State}，已复习 {entry.Review.Reps} 次。";

        DuplicateVisibility = Visibility.Visible;
        EditorVisibility = Visibility.Collapsed;

        if (!isFreshLookup)
        {
            _host.Vocabulary.RecordEncounter(entry.Id, DuplicateChannel.Manual, entry.Term);
            var refreshed = _host.Vocabulary.Lookup(entry.Language, entry.Term);
            if (refreshed is not null)
            {
                ShowDuplicate(refreshed, isFreshLookup: true);
            }
        }
    }

    public void RecordEncounterForDuplicate()
    {
        if (_duplicateId == 0)
        {
            return;
        }

        _host.Vocabulary.RecordEncounter(_duplicateId, DuplicateChannel.Manual, Term);
        var entry = _host.Entries.Get(_duplicateId);
        if (entry is not null)
        {
            ShowDuplicate(entry, isFreshLookup: true);
            StatusMessage = "已记录一次遇见，该词的复习会提前一点。";
        }
    }

    public void ResetDraft(string newTerm)
    {
        _editingId = 0;
        _duplicateId = 0;
        _nextTempReadingId = -1;
        _nextTempSenseId = -1;

        Term = newTerm;
        Readings.Clear();
        Senses.Clear();
        Examples.Clear();

        Readings.Add(new ReadingRowViewModel { Id = _nextTempReadingId--, Text = string.Empty, IsCommon = true });
        Senses.Add(new SenseRowViewModel { Id = _nextTempSenseId--, Gloss = string.Empty, IsVerified = true });
        Examples.Add(new ExampleRowViewModel { Text = string.Empty, IsVerified = true });
        Examples.Add(new ExampleRowViewModel { Text = string.Empty, IsVerified = true });

        RefreshReadingOptions();
        Validate();

        DuplicateVisibility = Visibility.Collapsed;
        EditorVisibility = Visibility.Visible;
        EditorHeader = "新建词条";
        PasteText = string.Empty;
        StatusMessage = string.IsNullOrWhiteSpace(newTerm)
            ? "手动录入：至少一条释义 + 目标每「读音×词性」2 条例句。"
            : $"手动录入「{newTerm}」。";
    }

    public void BeginEditing(Entry entry)
    {
        ApplyEntryToForm(entry);
        _editingId = entry.Id;
        DuplicateVisibility = Visibility.Collapsed;
        EditorVisibility = Visibility.Visible;
        EditorHeader = $"编辑：{entry.Term}";
        StatusMessage = "编辑已有词条。改动会覆盖原内容，复习进度保持不变。";
    }

    private void ApplyEntryToForm(Entry entry)
    {
        Term = entry.Term;
        Language = entry.Language;
        var index = Languages.ToList().FindIndex(l => l == entry.Language);
        if (index >= 0)
        {
            LanguageIndex = index;
        }

        KindIndex = entry.Kind == EntryKind.Grammar ? 1 : 0;
        Readings.Clear();
        foreach (var reading in entry.Readings)
        {
            Readings.Add(new ReadingRowViewModel
            {
                Id = reading.Id,
                Text = reading.Text,
                IsCommon = reading.IsCommon,
                Note = reading.Note,
            });
        }

        if (Readings.Count == 0)
        {
            Readings.Add(new ReadingRowViewModel { Id = _nextTempReadingId--, IsCommon = true });
        }

        _nextTempReadingId = Math.Min(_nextTempReadingId, Readings.Min(r => r.Id) - 1);

        Senses.Clear();
        foreach (var sense in entry.Senses)
        {
            Senses.Add(new SenseRowViewModel
            {
                Id = sense.Id,
                Gloss = sense.Gloss,
                IsVerified = sense.IsVerified,
            });
        }

        _nextTempSenseId = Senses.Count == 0 ? -1 : Math.Min(_nextTempSenseId, Senses.Min(s => s.Id) - 1);

        Examples.Clear();
        foreach (var example in entry.Examples)
        {
            Examples.Add(new ExampleRowViewModel
            {
                Id = example.Id,
                Text = example.Text,
                Translation = example.Translation,
                PartOfSpeech = example.PartOfSpeech,
                Note = example.Note,
                IsVerified = example.IsVerified,
                SelectedReading = null,
            });
        }

        RefreshReadingOptions();

        // 先建好选项，再按原有引用回填，避免下拉框清空
        for (var i = 0; i < entry.Examples.Count && i < Examples.Count; i++)
        {
            var readingId = entry.Examples[i].ReadingId;
            Examples[i].SelectedReading = ReadingOptionsCache.FirstOrDefault(o => o.Id == readingId);
        }

        for (var i = 0; i < entry.Senses.Count && i < Senses.Count; i++)
        {
            var readingId = entry.Senses[i].ReadingId;
            Senses[i].SelectedReading = ReadingOptionsCache.FirstOrDefault(o => o.Id == readingId);
        }

        Validate();
    }

    private readonly List<ReadingOption> ReadingOptionsCache = new();

    public void AddReading()
    {
        Readings.Add(new ReadingRowViewModel { Id = _nextTempReadingId--, IsCommon = true });
        RefreshReadingOptions();
    }

    public void RemoveReading(ReadingRowViewModel row)
    {
        Readings.Remove(row);
        RefreshReadingOptions();
    }

    public void AddSense()
    {
        Senses.Add(new SenseRowViewModel { Id = _nextTempSenseId--, IsVerified = true });
        RefreshReadingOptions();
    }

    public void RemoveSense(SenseRowViewModel row)
    {
        Senses.Remove(row);
        RefreshReadingOptions();
    }

    public void AddExample()
    {
        Examples.Add(new ExampleRowViewModel { IsVerified = true });
        RefreshReadingOptions();
    }

    public void RemoveExample(ExampleRowViewModel row)
    {
        Examples.Remove(row);
        RefreshReadingOptions();
    }

    public void RefreshReadingOptions()
    {
        ReadingOptionsCache.Clear();
        foreach (var reading in Readings.Where(r => !string.IsNullOrWhiteSpace(r.Text)))
        {
            ReadingOptionsCache.Add(new ReadingOption(reading.Id, reading.Text));
        }

        foreach (var row in Senses)
        {
            var keep = row.SelectedReading?.Id;
            row.ReadingOptions.Clear();
            foreach (var option in ReadingOptionsCache)
            {
                row.ReadingOptions.Add(option);
            }

            if (keep is { } id)
            {
                row.SelectedReading = row.ReadingOptions.FirstOrDefault(o => o.Id == id);
            }
        }

        foreach (var row in Examples)
        {
            var keep = row.SelectedReading?.Id;
            row.ReadingOptions.Clear();
            foreach (var option in ReadingOptionsCache)
            {
                row.ReadingOptions.Add(option);
            }

            if (keep is { } id)
            {
                row.SelectedReading = row.ReadingOptions.FirstOrDefault(o => o.Id == id);
            }
        }
    }

    public Entry BuildEntry()
    {
        var entry = new Entry
        {
            Id = _editingId,
            Term = Term,
            Language = Language,
            Kind = CurrentKind,
            Source = _pendingReplacement?.Source ?? EntrySource.Manual,
        };

        foreach (var row in Readings.Where(r => !string.IsNullOrWhiteSpace(r.Text)))
        {
            entry.Readings.Add(new Reading
            {
                Id = row.Id,
                Text = row.Text.Trim(),
                IsCommon = row.IsCommon,
                Note = row.Note,
                Ordinal = entry.Readings.Count,
            });
        }

        foreach (var row in Senses.Where(s => !string.IsNullOrWhiteSpace(s.Gloss)))
        {
            entry.Senses.Add(new Sense
            {
                Id = row.Id,
                ReadingId = row.SelectedReading?.Id,
                Gloss = row.Gloss.Trim(),
                IsVerified = row.IsVerified,
                Ordinal = entry.Senses.Count,
            });
        }

        foreach (var row in Examples.Where(e => !string.IsNullOrWhiteSpace(e.Text)))
        {
            entry.Examples.Add(new Example
            {
                Id = row.Id,
                ReadingId = row.SelectedReading?.Id,
                PartOfSpeech = row.PartOfSpeech,
                Text = row.Text.Trim(),
                Translation = row.Translation,
                Note = row.Note,
                Source = _pendingReplacement?.Source ?? EntrySource.Manual,
                IsVerified = row.IsVerified,
                Ordinal = entry.Examples.Count,
            });
        }

        return entry;
    }

    public ValidationResult Validate()
    {
        RefreshReadingOptions();
        var entry = BuildEntry();
        var result = _host.Vocabulary.Validator.Validate(entry);
        var coverage = _host.Vocabulary.Validator.Coverage(entry);

        var warnings = result.Warnings.Select(w => "⚠ " + w.Message).ToList();
        var errors = result.Errors.Select(e => "✕ " + e.Message).ToList();
        ValidationSummary = string.Join(Environment.NewLine, errors.Concat(warnings));

        var target = _host.Settings.ExamplesPerReadingPos;
        CoverageSummary = coverage.Count == 0
            ? "例句覆盖：暂无"
            : $"例句覆盖（目标 {target} 条/格）：" + string.Join("；", coverage.Select(c => c.Display));

        var gaps = ComputeGaps();
        var readyForTopUp = gaps.Count > 0 &&
                            !string.IsNullOrWhiteSpace(Term) &&
                            Senses.Any(s => !string.IsNullOrWhiteSpace(s.Gloss));
        TopUpVisibility = readyForTopUp ? Visibility.Visible : Visibility.Collapsed;

        return result;
    }

    /// <summary>算出每个「读音 × 词性」还差几条例句，用于第二轮补例句请求。</summary>
    public IReadOnlyList<ExampleGap> ComputeGaps()
    {
        var target = _host.Settings.ExamplesPerReadingPos;
        var entry = BuildEntry();
        var cells = _host.Vocabulary.Validator.Coverage(entry);

        return cells
            .Where(c => c.Count < target)
            .Select(c => new ExampleGap(c.Reading, c.PartOfSpeech, c.Count, target - c.Count))
            .ToList();
    }

    /// <summary>
    /// 只补例句的第二轮请求。凑不满时模型会按提示词说明原因，这里如实汇报还差什么，
    /// 而不是假装补齐了。
    /// </summary>
    public async Task TopUpExamplesAsync()
    {
        var gaps = ComputeGaps();
        if (gaps.Count == 0)
        {
            StatusMessage = "例句数量已经达到目标，不需要补充。";
            return;
        }

        if (!_host.Settings.LlmEnabled)
        {
            CopyTopUpPrompt(gaps);
            return;
        }

        IsBusy = true;
        StatusMessage = "正在请求模型补充例句…";
        try
        {
            using var client = _host.CreateLlmClient();
            var raw = await client.CompleteAsync(
                ExampleTopUpPromptBuilder.SystemPrompt(),
                ExampleTopUpPromptBuilder.UserPrompt(Term, Language, gaps, BuildGlossMap()));

            ApplyTopUpResult(raw);
        }
        catch (LlmException ex)
        {
            StatusMessage = "补例句失败：" + ex.Message + Environment.NewLine + "可以手动补，或复制提示词去网页版。";
        }
        catch (Exception ex)
        {
            _host.Logger.Error("补例句失败", ex);
            StatusMessage = "补例句失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void CopyTopUpPrompt(IReadOnlyList<ExampleGap>? gaps = null)
    {
        gaps ??= ComputeGaps();
        if (gaps.Count == 0)
        {
            StatusMessage = "例句数量已经达到目标，不需要补充。";
            return;
        }

        var text = ExampleTopUpPromptBuilder.ClipboardPrompt(Term, Language, gaps);
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        StatusMessage = "补例句的提示词已复制。发给网页版模型后，把返回内容粘到下面的框里，点「解析并填入表单」即可——会自动识别为补例句。";
    }

    private IReadOnlyDictionary<string, string[]> BuildGlossMap()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var reading in Readings.Where(r => !string.IsNullOrWhiteSpace(r.Text)))
        {
            var glosses = Senses
                .Where(s => s.SelectedReading?.Id == reading.Id && !string.IsNullOrWhiteSpace(s.Gloss))
                .Select(s => s.Gloss.Trim())
                .ToArray();

            if (glosses.Length > 0)
            {
                map[reading.Text.Trim()] = glosses;
            }
        }

        var unbound = Senses
            .Where(s => s.SelectedReading is null && !string.IsNullOrWhiteSpace(s.Gloss))
            .Select(s => s.Gloss.Trim())
            .ToArray();

        if (unbound.Length > 0)
        {
            map[string.Empty] = unbound;
        }

        return map;
    }

    /// <summary>把补到的例句追加到表单末尾；同文重复的跳过，并按缺口截断（多给的不收）。</summary>
    public int MergeAdditions(IReadOnlyList<ExampleAddition> additions, IReadOnlyList<ExampleGap>? gaps = null)
    {
        var existing = new HashSet<string>(
            Examples.Where(e => !string.IsNullOrWhiteSpace(e.Text))
                .Select(e => Core.Text.TermNormalizer.Normalize(e.Text)),
            StringComparer.Ordinal);

        // 缺口预算：模型偶尔会多给，这里按「还差几条」封顶，避免无意义堆积
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gap in gaps ?? Array.Empty<ExampleGap>())
        {
            budget[BucketKey(gap.Reading, gap.PartOfSpeech)] = gap.Need;
        }

        var added = 0;
        foreach (var addition in additions)
        {
            var key = Core.Text.TermNormalizer.Normalize(addition.Text);
            if (key.Length == 0 || !existing.Add(key))
            {
                continue;
            }

            var bucket = BucketKey(addition.Reading, addition.PartOfSpeech);
            if (budget.TryGetValue(bucket, out var left))
            {
                if (left <= 0)
                {
                    continue;
                }

                budget[bucket] = left - 1;
            }

            var readingRow = MatchReading(addition.Reading);
            Examples.Add(new ExampleRowViewModel
            {
                Text = addition.Text,
                Translation = addition.Translation,
                PartOfSpeech = string.IsNullOrWhiteSpace(addition.PartOfSpeech) ? null : addition.PartOfSpeech,
                Note = addition.Note,
                IsVerified = false,
                SelectedReading = readingRow?.Id is { } id
                    ? new ReadingOption(id, readingRow.Text.Trim())
                    : null,
            });
            added++;
        }

        RefreshReadingOptions();
        return added;
    }

    private static string BucketKey(string? reading, string? pos) =>
        $"{Core.Text.TermNormalizer.Normalize(reading)}|{Core.Text.TermNormalizer.Normalize(pos)}";

    private ReadingRowViewModel? MatchReading(string? reading)
    {
        var key = Core.Text.TermNormalizer.Normalize(reading);
        if (key.Length == 0)
        {
            return null;
        }

        return Readings.FirstOrDefault(r => Core.Text.TermNormalizer.Normalize(r.Text) == key);
    }

    private void ApplyTopUpResult(string raw)
    {
        var parsed = ExampleTopUpParser.Parse(raw);
        if (!parsed.Ok)
        {
            StatusMessage = "补例句解析失败：" + parsed.Error;
            return;
        }

        var gaps = ComputeGaps();
        var added = MergeAdditions(parsed.Additions, gaps);
        var remaining = ComputeGaps();
        Validate();

        if (remaining.Count == 0)
        {
            StatusMessage = $"已补入 {added} 条例句，现在每个「读音 × 词性」都达到目标了。请核对内容后保存。";
            return;
        }

        var still = string.Join("；", remaining.Select(g =>
            $"{(string.IsNullOrEmpty(g.Reading) ? "（无读音）" : g.Reading)} · " +
            $"{(string.IsNullOrEmpty(g.PartOfSpeech) ? "未标词性" : g.PartOfSpeech)} 还差 {g.Need} 条"));

        StatusMessage = added == 0
            ? "模型没有补出新的例句。" + Environment.NewLine + "还差：" + still + Environment.NewLine + "可以再试一次，或手动补。"
            : $"已补入 {added} 条例句，但仍有缺口：{still}。可以再点一次「补齐例句」，或手动补。";
    }

    public SaveResult Save()
    {
        var entry = BuildEntry();
        var result = _host.Vocabulary.Save(entry, DuplicateChannel.Manual);

        switch (result.Status)
        {
            case SaveStatus.Saved:
                _editingId = result.EntryId;
                _pendingReplacement = null;
                StatusMessage = $"已保存「{entry.Term}」，并加入复习队列。";
                Term = entry.Term;
                break;
            case SaveStatus.Duplicate:
                ShowDuplicate(result.Duplicate!, isFreshLookup: true);
                StatusMessage = "这个词已经在库里了，已记录一次遇见。没有新建重复条目。";
                break;
            default:
                StatusMessage = "还有错误需要修正：" + Environment.NewLine + ValidationSummary;
                break;
        }

        return result;
    }

    public string BuildClipboardPrompt() =>
        EntryPromptBuilder.ClipboardPrompt(Term, Language, CurrentKind);

    public async Task FillWithLlmAsync()
    {
        if (string.IsNullOrWhiteSpace(Term))
        {
            StatusMessage = "请先输入词条。";
            return;
        }

        if (!_host.Settings.LlmEnabled)
        {
            StatusMessage = "设置里已关闭大模型，请用「复制提示词」或手动录入。";
            return;
        }

        IsBusy = true;
        StatusMessage = "正在请求大模型…";
        try
        {
            using var client = _host.CreateLlmClient();
            var raw = await client.CompleteAsync(
                EntryPromptBuilder.SystemPrompt(Language, CurrentKind),
                EntryPromptBuilder.UserPrompt(Term, Language, CurrentKind));

            ApplyLlmResult(raw, EntrySource.LlmAuto);
        }
        catch (LlmException ex)
        {
            StatusMessage = "大模型调用失败：" + ex.Message + Environment.NewLine + "可以改用「复制提示词」或手动填写。";
        }
        catch (Exception ex)
        {
            _host.Logger.Error("大模型填充失败", ex);
            StatusMessage = "大模型调用失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ParsePasted()
    {
        if (string.IsNullOrWhiteSpace(PasteText))
        {
            StatusMessage = "请先把大模型返回的内容粘贴到下面的框里。";
            return;
        }

        // 自动识别：带 additions 的是「补例句」的回复，其余按词条解析
        var json = EntryJsonParser.ExtractJsonObject(PasteText);
        if (json is not null && json.Contains("\"additions\"", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTopUpResult(PasteText);
            return;
        }

        ApplyLlmResult(PasteText, EntrySource.LlmPaste);
    }

    private void ApplyLlmResult(string raw, EntrySource source)
    {
        var parsed = EntryJsonParser.Parse(raw, Language, CurrentKind, source);
        if (!parsed.Ok)
        {
            StatusMessage = "解析失败：" + parsed.Error + Environment.NewLine + "内容已保留，可手动修改或重新生成。";
            return;
        }

        var entry = parsed.Entry!;
        _pendingReplacement = entry;
        _editingId = 0;

        Term = entry.Term.Length == 0 ? Term : entry.Term;
        ApplyEntryToForm(entry);
        EditorVisibility = Visibility.Visible;
        DuplicateVisibility = Visibility.Collapsed;

        var validation = Validate();
        StatusMessage = validation.HasWarnings
            ? "已填入模型结果，请逐条核对（内容标记为未校对）。有例句不足的提示，可重新生成或手动补。"
            : "已填入模型结果，请核对后保存。";
    }
}
