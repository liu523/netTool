namespace LYFZ.NetDiag.Diagnostics;

internal static class OutputPaths
{
    public static string GetDefaultLogDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "利亚方舟网络诊断日志"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "利亚方舟网络诊断日志"),
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(Path.GetTempPath(), "LYFZ-NetDiag")
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // Try the next safe location.
            }
        }

        throw new IOException("找不到可写入诊断日志的目录。");
    }

    public static string SanitizeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "未填写门店" : cleaned;
    }
}
