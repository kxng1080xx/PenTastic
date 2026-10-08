using System.Diagnostics;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

static class Adb
{
    public static string? Find()
    {
        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        return new[] { Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe") }
            .Concat(path.Select(d => Path.Combine(d.Trim('"'), "adb.exe")))
            .Append(Path.Combine(sdk, "platform-tools", "adb.exe"))
            .FirstOrDefault(File.Exists);
    }

    public static void Watch(string adb, Action<string> status, CancellationToken ct)
    {
        Log.Write($"adb: {adb}");
        var known = new HashSet<string>();
        while (!ct.IsCancellationRequested)
        {
            var devices = Run(adb, "devices").Split('\n').Skip(1)
                .Select(l => l.Trim().Split('\t')).Where(p => p.Length == 2).ToList();
            var ready = devices.Where(p => p[1] == "device").Select(p => p[0]).ToList();

            foreach (var serial in ready)
            {
                var result = Run(adb, $"-s {serial} reverse tcp:{Protocol.Port} tcp:{Protocol.Port}").Trim();
                if (known.Add(serial)) Log.Write($"adb reverse for {serial}: {(result == "" ? "ok" : result)}");
            }
            known.IntersectWith(ready);

            status(ready.Count > 0 ? "USB: phone ready"
                : devices.Any(p => p[1] == "unauthorized") ? "USB: allow USB debugging on the phone"
                : "USB: no phone");
            ct.WaitHandle.WaitOne(2000);
        }
    }

    static string Run(string adb, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(adb, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) p.Kill();
            return Task.WhenAll(stdout, stderr).Wait(2000) ? stdout.Result + stderr.Result : "";
        }
        catch (Exception e)
        {
            Log.Write($"adb {args} failed: {e.Message}");
            return "";
        }
    }
}
