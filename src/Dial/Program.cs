using Chassis.Core.Versioning;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Dial;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey($"Chassis_SingleInstance_{AppVersion.EntryAssemblyName}");
        if (!mainInstance.IsCurrent)
        {
            mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return;
        }

        mainInstance.Activated += (_, _) => App.OnExternalInstanceActivated();

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
