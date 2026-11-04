using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WordBase.App.ViewModels;

namespace WordBase.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel(App.Host);
        DataContext = ViewModel;
    }

    public SettingsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Save(ApiKeyBox.Password);
    }

    private async void OnTestLlmClick(object sender, RoutedEventArgs e) => await ViewModel.TestLlmAsync();

    private async void OnSelfTestClick(object sender, RoutedEventArgs e) => await ViewModel.RunNotificationSelfTestAsync();

    private void OnRefreshDiagnosticsClick(object sender, RoutedEventArgs e) => ViewModel.RefreshDiagnostics();

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{App.Host.Paths.DataDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ViewModel.GeneralStatus = "打开目录失败：" + ex.Message;
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.ExportBackup();
        }
        catch (Exception ex)
        {
            ViewModel.GeneralStatus = "导出失败：" + ex.Message;
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "导入备份",
                Content = $"将用备份覆盖当前数据库和设置。当前数据会先自动备份一份。\n\n{file.Path}",
                PrimaryButtonText = "覆盖导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ViewModel.ImportBackup(file.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.GeneralStatus = "导入失败：" + ex.Message;
        }
    }
}
