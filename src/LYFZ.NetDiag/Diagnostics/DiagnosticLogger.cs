using System.Text;

namespace LYFZ.NetDiag.Diagnostics;

internal sealed class DiagnosticLogger : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IProgress<DiagnosticProgress>? _progress;

    public DiagnosticLogger(string path, IProgress<DiagnosticProgress>? progress)
    {
        Path = path;
        _progress = progress;
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
    }

    public string Path { get; }

    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(text);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Report(string message, int completed, int total) =>
        _progress?.Report(new DiagnosticProgress(message, completed, total));

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _writer.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
