using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WordBase.App.ViewModels;
using WordBase.Core.Models;

namespace WordBase.App.Views;

public sealed partial class ReviewPage : Page
{
    public ReviewPage()
    {
        InitializeComponent();
        ViewModel = new ReviewViewModel(App.Host);
        Session = new ReviewSessionViewModel(App.Host);
        Session.SessionCompleted += OnSessionCompleted;

        DataContext = ViewModel;
        SessionPanel.DataContext = Session;
    }

    public ReviewViewModel ViewModel { get; }
    public ReviewSessionViewModel Session { get; }

    public bool IsSessionActive => SessionPanel.Visibility == Visibility.Visible;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ShowIdle();
    }

    /// <summary>在本页内开始一轮复习；entryId 非空时只复习这一个词。</summary>
    public void BeginSession(long? entryId = null, bool? presetKnown = null)
    {
        IdlePanel.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = Visibility.Visible;
        Session.Start(entryId, presetKnown);
    }

    public void ShowIdle()
    {
        SessionPanel.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Visible;
        ViewModel.Refresh();
    }

    private void OnSessionCompleted()
    {
        App.Host.Scheduler?.ResetCounters();
        ViewModel.Refresh();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void OnStartClick(object sender, RoutedEventArgs e) => BeginSession();

    private void OnEndSessionClick(object sender, RoutedEventArgs e) => ShowIdle();

    private void OnKnownClick(object sender, RoutedEventArgs e) => Session.ChooseKnown();

    private void OnUnknownClick(object sender, RoutedEventArgs e) => Session.ChooseUnknown();

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuizOptionViewModel option })
        {
            Session.SelectOption(option);
        }
    }

    private void OnRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<Rating>(tag, out var rating))
        {
            Session.Rate(rating);
        }
    }

    private void OnContinueClick(object sender, RoutedEventArgs e) => Session.Continue();
}
