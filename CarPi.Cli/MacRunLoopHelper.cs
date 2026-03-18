#if MACCATALYST
/// <summary>
/// CoreBluetooth requires a CFRunLoop on the main thread to dispatch callbacks.
/// A console app doesn't have one, so we run Program logic on a background thread
/// and pump the run loop on main until it finishes.
/// </summary>
public static class MacRunLoopHelper
{
    public static Task<int> RunWithRunLoopAsync(string[] args)
    {
        var tcs = new TaskCompletionSource<int>();

        var thread = new Thread(() =>
        {
            try
            {
                var result = ProgramMain.RunAsync(args).GetAwaiter().GetResult();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                CoreFoundation.CFRunLoop.Main.Stop();
            }
        })
        { IsBackground = true };
        thread.Start();

        CoreFoundation.CFRunLoop.Main.Run();

        return tcs.Task;
    }
}
#endif
