using System.Diagnostics;
using System.Globalization;

namespace WarpforgePatcher;

/// <summary>
/// The progress bar at the bottom of the console (0.4.3; players asked for it): what is happening, how far along, and for
/// downloads the size and speed. MainActivity draws it; everything else only calls Show and Hide (from any thread).
/// The log keeps only a start and an end line per download, not the bar's updates.
/// </summary>
internal static class Progress
{
    /// <summary>Set by MainActivity: (label, fraction 0..1 or -1 when unknown, detail).</summary>
    public static Action<string, double, string>? Sink;
    public static Action? HideSink;

    public static void Show(string label, double fraction, string detail = "") => Sink?.Invoke(label, fraction, detail);
    public static void Hide() => HideSink?.Invoke();

    /// <summary>No data for this long ends a download (before 0.4.3 a stalled download sat there until the 10-minute limit).</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies a download to <paramref name="output"/>, showing the bar. Ends with <see cref="Stalled"/> when nothing arrives
    /// for <see cref="StallAfter"/>. Returns the number of bytes copied.
    /// </summary>
    public static async Task<long> Download(Stream input, Stream output, long total, string label, string logName, Action<string> log)
    {
        var buffer = new byte[81920];
        long done = 0, shownAt = -1000, windowStart = 0, windowBytes = 0;
        double speed = 0;
        var clock = Stopwatch.StartNew();
        Show(label, total > 0 ? 0 : -1, Detail(0, total, -1));
        while (true)
        {
            var read = input.ReadAsync(buffer, 0, buffer.Length);
            if (await Task.WhenAny(read, Task.Delay(StallAfter)) != read)
            {
                log($"{logName}: no data for {StallAfter.TotalSeconds:F0} s after {Mb(done)} of {(total > 0 ? Mb(total) : "?")} MB; stopped");
                try { input.Dispose(); } catch { }
                Hide();
                throw new Stalled();
            }
            int n = await read;
            if (n == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n));
            done += n;
            windowBytes += n;
            long now = clock.ElapsedMilliseconds;
            if (now - windowStart >= 1000) { speed = windowBytes * 1000.0 / (now - windowStart); windowStart = now; windowBytes = 0; }
            if (now - shownAt >= 200) { shownAt = now; Show(label, total > 0 ? (double)done / total : -1, Detail(done, total, now < 1000 ? -1 : speed)); }
        }
        double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        log($"{logName}: {Mb(done)} MB in {seconds:F1} s ({Speed(done / seconds)})");
        Hide();
        return done;
    }

    /// <summary>A local copy (unpacking, handing files to Android) with the bar; <paramref name="before"/> bytes of <paramref name="total"/> were done earlier.</summary>
    public static long Copy(Stream input, Stream output, long before, long total, string label)
    {
        var buffer = new byte[1 << 20];
        long done = before, shownAt = -1000;
        var clock = Stopwatch.StartNew();
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
            done += n;
            long now = clock.ElapsedMilliseconds;
            if (now - shownAt >= 200) { shownAt = now; Show(label, total > 0 ? (double)done / total : -1, total > 0 ? $"{Mb(done)} / {Mb(total)} MB" : $"{Mb(done)} MB"); }
        }
        return done;
    }

    private static string Detail(long done, long total, double speed) =>
        (total > 0 ? $"{Mb(done)} / {Mb(total)} MB" : $"{Mb(done)} MB") + (speed >= 0 ? " · " + Speed(speed) : "");

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);

    private static string Speed(double bytesPerSecond) => bytesPerSecond >= 1048576
        ? (bytesPerSecond / 1048576).ToString("F1", CultureInfo.InvariantCulture) + " MB/s"
        : (bytesPerSecond / 1024).ToString("F0", CultureInfo.InvariantCulture) + " KB/s";

    /// <summary>A download that stopped sending data; callers turn it into a message for the player.</summary>
    public sealed class Stalled() : Exception("no data for 30 s") { }
}
