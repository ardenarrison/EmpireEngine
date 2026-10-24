using Avalonia;
using EmpireHub;

// The new Hub of an update is started with --apply-update; it only swaps the files and starts the Hub again (see HubUpdater).
if (args.Length > 0 && args[0] == HubUpdater.ApplyArgument)
    return HubUpdater.RunApply(args);

return AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .LogToTrace()
    .StartWithClassicDesktopLifetime(args);
