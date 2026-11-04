using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WordBase.App.ViewModels;
using WordBase.Core.Models;

namespace WordBase.App.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = new LibraryViewModel(App.Host);
        DataContext = ViewModel;
    }

    public LibraryViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntrySummary summary)
        {
            Frame.Navigate(typeof(EntryEditorPage), summary.Id);
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EntrySummary summary })
        {
            Frame.Navigate(typeof(EntryEditorPage), summary.Id);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EntrySummary summary })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除词条",
            Content = $"确定删除「{summary.Term}」吗？连同释义、例句和复习记录一起删除，且不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Delete(summary.Id);
        }
    }
}
