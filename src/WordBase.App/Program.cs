using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace WordBase.App;

public static class Program
{
    private const string InstanceKey = "WordBase.SingleInstance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
        {
            return 0;
        }

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            new App();
        });

        return 0;
    }

    /// <summary>单实例：第二次启动（含点通知）只把激活参数转给已有进程，然后退出。</summary>
    private static bool DecideRedirection()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (!main.IsCurrent)
        {
            main.RedirectActivationToAsync(activatedArgs).AsTask().GetAwaiter().GetResult();
            return true;
        }

        App.MainInstance = main;
        main.Activated += (_, e) => App.HandleExternalActivation(e);
        return false;
    }
}
