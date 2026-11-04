using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WordBase.App.ViewModels;
using WordBase.Core.Models;
using Windows.Graphics;

namespace WordBase.App.Views;

public sealed partial class ReviewSessionWindow : Window
{
    public ReviewSessionWindow()
    {
        InitializeComponent();
        ViewModel = new ReviewSessionViewModel(App.Host);
        ViewModel.SessionCompleted += OnSessionCompleted;
        // Window 不是 FrameworkElement，绑定挂到 Content 的 DataContext 上
        ((FrameworkElement)Content).DataContext = ViewModel;

        AppWindow.Resize(new SizeInt32(900, 700));
        AppWindow.Closing += OnClosing;
    }

    public ReviewSessionViewModel ViewModel { get; }

    public event Action? Completed;

    private void OnSessionCompleted()
    {
        Completed?.Invoke();
        App.Host.Scheduler?.ResetCounters();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        Completed?.Invoke();
    }

    private void OnKnownClick(object sender, RoutedEventArgs e) => ViewModel.ChooseKnown();

    private void OnUnknownClick(object sender, RoutedEventArgs e) => ViewModel.ChooseUnknown();

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuizOptionViewModel option })
        {
            ViewModel.SelectOption(option);
        }
    }

    private void OnRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<Rating>(tag, out var rating))
        {
            ViewModel.Rate(rating);
        }
    }

    private void OnContinueClick(object sender, RoutedEventArgs e) => ViewModel.Continue();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
