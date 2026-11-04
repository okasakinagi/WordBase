using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WordBase.App.ViewModels;

namespace WordBase.App.Views;

public sealed partial class EntryEditorPage : Page
{
    public EntryEditorPage()
    {
        InitializeComponent();
        ViewModel = new EntryEditorViewModel(App.Host);
        DataContext = ViewModel;
    }

    public EntryEditorViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is long id)
        {
            var entry = App.Host.Entries.Get(id);
            if (entry is not null)
            {
                ViewModel.BeginEditing(entry);
            }
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => ViewModel.Search();

    private void OnTermKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.Search();
        }
    }

    private void OnRecordEncounterClick(object sender, RoutedEventArgs e) => ViewModel.RecordEncounterForDuplicate();

    private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.ResetDraft(ViewModel.Term);

    private void OnAddReadingClick(object sender, RoutedEventArgs e) => ViewModel.AddReading();

    private void OnRemoveReadingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReadingRowViewModel row })
        {
            ViewModel.RemoveReading(row);
        }
    }

    private void OnReadingTextChanged(object sender, TextChangedEventArgs e) => ViewModel.RefreshReadingOptions();

    private void OnAddSenseClick(object sender, RoutedEventArgs e) => ViewModel.AddSense();

    private void OnRemoveSenseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SenseRowViewModel row })
        {
            ViewModel.RemoveSense(row);
        }
    }

    private void OnAddExampleClick(object sender, RoutedEventArgs e) => ViewModel.AddExample();

    private void OnRemoveExampleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExampleRowViewModel row })
        {
            ViewModel.RemoveExample(row);
        }
    }

    private void OnValidateClick(object sender, RoutedEventArgs e) => ViewModel.Validate();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshReadingOptions();
        var result = ViewModel.Save();
        if (result.Status == Core.Services.SaveStatus.Saved)
        {
            await ShowDialogAsync("已保存", $"「{ViewModel.Term}」已加入复习队列。");
        }
    }

    private async void OnLlmClick(object sender, RoutedEventArgs e) => await ViewModel.FillWithLlmAsync();

    private async void OnTopUpClick(object sender, RoutedEventArgs e) => await ViewModel.TopUpExamplesAsync();

    private void OnCopyPromptClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.BuildClipboardPrompt());
        Clipboard.SetContent(package);
        ViewModel.StatusMessage = "提示词已复制。到网页版模型里粘贴，再把回复粘回上面的「粘贴大模型回复」框。";
    }

    private void OnParsePasteClick(object sender, RoutedEventArgs e) => ViewModel.ParsePasted();

    private async Task ShowDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
