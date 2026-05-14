using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Lookout;

/// <summary>
/// Custom entry point. Replaces the XAML-generated Main so we can enforce
/// single-instance behavior before the UI spins up.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (!DecideRedirection())
        {
            // Another instance already owns the single-instance key.
            // We've redirected our activation to it; exit quietly.
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <returns>true if this process should continue as the primary instance.</returns>
    private static bool DecideRedirection()
    {
        var keyInstance = AppInstance.FindOrRegisterForKey("Lookout-Single-Instance");

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return true;
        }

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        keyInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
        return false;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        // A second launch was redirected here — surface the existing window.
        App.Current?.HandleRedirectedActivation();
    }
}
