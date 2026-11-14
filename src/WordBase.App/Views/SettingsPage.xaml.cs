using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WordBase.App.ViewModels;
using WordBase.Data;

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

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        _ = ImportAsync();
    }

    private async Task ImportAsync()
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

            BackupInfo info;
            try
            {
                info = App.Host.Backups.Inspect(file.Path);
            }
            catch (Exception ex)
            {
                ViewModel.GeneralStatus = "读取备份失败：" + ex.Message;
                return;
            }

            if (!info.HasDatabase)
            {
                await ShowMessageAsync("不是有效的备份", "这个压缩包里没有 wordbase.db，看起来不是 WordBase 的备份。");
                return;
            }

            var current = App.Host.Backups.CountCurrentEntries();
            var incoming = info.EntryCount;

            // 空库覆盖有数据的库 = 静默丢数据。这里必须拦一下。
            if (incoming == 0 && current > 0)
            {
                var warn = new ContentDialog
                {
                    Title = "这个备份是空的",
                    Content = $"备份里有 0 个词条，而你当前有 {current} 个。\n\n" +
                              "如果你之前导入失败过，这条很可能是自动生成的「回滚包」，不是你的数据。\n" +
                              "继续导入会清空现有的词库。确定要继续吗？",
                    PrimaryButtonText = "仍然导入（清空现有词库）",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };

                if (await warn.ShowAsync() != ContentDialogResult.Primary)
                {
                    ViewModel.GeneralStatus = "已取消导入。";
                    return;
                }
            }

            var incomingText = incoming < 0 ? "（无法统计）" : $"{incoming} 个词条";
            var dialog = new ContentDialog
            {
                Title = "导入备份",
                Content = $"将用这份备份覆盖当前的数据库和设置。\n\n" +
                          $"备份内容：{incomingText}\n" +
                          $"当前词库：{current} 个词条\n\n" +
                          "导入前会自动把现在的数据另存一份回滚包。",
                PrimaryButtonText = "覆盖导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            ViewModel.ImportBackup(file.Path);
        }
        catch (Exception ex)
        {
            ViewModel.GeneralStatus = "导入失败：" + ex.Message;
        }
    }

    private async Task ShowMessageAsync(string title, string content)
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
