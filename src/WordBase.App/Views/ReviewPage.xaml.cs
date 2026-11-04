using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WordBase.App.ViewModels;

namespace WordBase.App.Views;

public sealed partial class ReviewPage : Page
{
    public ReviewPage()
    {
        InitializeComponent();
        ViewModel = new ReviewViewModel(App.Host);
        DataContext = ViewModel;
    }

    public ReviewViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void OnStartClick(object sender, RoutedEventArgs e) => App.StartReviewSession();
}
