namespace WindowsIsland;

/// <summary>
/// Serialises brightness writes and collapses bursts into the latest value.
///
/// Writing brightness spawns a PowerShell process (~300 ms), so a fast scroll
/// used to queue dozens of processes and stalled the whole app. Here we keep a
/// single worker: while a write is running, newer requests only update the
/// pending target, and the worker always converges on the final value.
/// </summary>
internal static class BrightnessWriter
{
    private static readonly object Gate = new();
    private static int _pending = -1;
    private static bool _running;

    public static void Request(int percent)
    {
        lock (Gate)
        {
            _pending = Math.Clamp(percent, 0, 100);

            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(LoopAsync);
    }

    private static async Task LoopAsync()
    {
        while (true)
        {
            int target;

            lock (Gate)
            {
                if (_pending < 0)
                {
                    _running = false;
                    return;
                }

                target = _pending;
                _pending = -1;
            }

            try
            {
                BrightnessController.TrySetPercent(target);
            }
            catch
            {
                // never let the worker die
            }

            // Small gap so consecutive writes don't fight over the display.
            await Task.Delay(120).ConfigureAwait(false);
        }
    }
}
