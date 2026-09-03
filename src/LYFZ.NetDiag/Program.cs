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
        var extraDomainInputs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputDirectory = args[++i];
                continue;
            }

            if (string.Equals(args[i], "--monitor-minutes", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length && int.TryParse(args[i + 1], out var minutes) && minutes is 1 or 5 or 10)
            {
                monitorDuration = TimeSpan.FromMinutes(minutes);
                i++;
                continue;
            }

            if (string.Equals(args[i], "--domains", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                extraDomainInputs.Add(args[++i]);
                continue;
            }

            if (args[i].StartsWith("--domains=", StringComparison.OrdinalIgnoreCase))
            {
                extraDomainInputs.Add(args[i]["--domains=".Length..]);
            }
        }

        outputDirectory ??= OutputPaths.GetDefaultLogDirectory();
        var extraDomains = DomainCatalog.ParseExtraDomains(string.Join(',', extraDomainInputs));
        var runner = new DiagnosticRunner();
        await runner.RunAsync(
            new DiagnosticRunOptions(
                outputDirectory,
                "自动测试",
                "",
                false,
                monitorDuration,
                TimeSpan.FromSeconds(10),
                extraDomains),
            null,
            CancellationToken.None);
    }
}
