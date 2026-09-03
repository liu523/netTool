using LYFZ.NetDiag.Diagnostics;

namespace LYFZ.NetDiag;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--auto", StringComparison.OrdinalIgnoreCase)))
        {
            RunAutomaticAsync(args).GetAwaiter().GetResult();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static async Task RunAutomaticAsync(string[] args)
    {
        string? outputDirectory = null;
        var monitorDuration = TimeSpan.Zero;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase))
            {
                outputDirectory = args[i + 1];
            }

            if (string.Equals(args[i], "--monitor-minutes", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var minutes) && minutes is 5 or 10)
            {
                monitorDuration = TimeSpan.FromMinutes(minutes);
            }
        }

        outputDirectory ??= OutputPaths.GetDefaultLogDirectory();
        var runner = new DiagnosticRunner();
        await runner.RunAsync(
            new DiagnosticRunOptions(
                outputDirectory,
                "自动测试",
                "",
                false,
                monitorDuration,
                TimeSpan.FromSeconds(10)),
            null,
            CancellationToken.None);
    }
}
