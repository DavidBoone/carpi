// Entry point: on macOS, pump CFRunLoop on main thread for CoreBluetooth.
// On Linux, run directly.
#if MACCATALYST
return await MacRunLoopHelper.RunWithRunLoopAsync(args);
#else
return await ProgramMain.RunAsync(args);
#endif
