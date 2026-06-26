using System.Text;

namespace MeshCoreNet;

internal static class FullStackLogger
{
    private static IDisposable? _handle;

    public static void Initialize(string? logDirectory = null)
    {
        if (_handle is not null)
        {
            return;
        }

        var directory = string.IsNullOrWhiteSpace(logDirectory)
            ? Environment.GetEnvironmentVariable("MESHCORE_LOG_DIR") ?? "/var/log/meshcore-netcore"
            : logDirectory;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "meshcore-netcore.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var fileWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            var consoleOut = Console.Out;
            var consoleError = Console.Error;
            var multiOut = TextWriter.Synchronized(new TeeTextWriter(consoleOut, fileWriter));
            var multiErr = TextWriter.Synchronized(new TeeTextWriter(consoleError, fileWriter));
            Console.SetOut(multiOut);
            Console.SetError(multiErr);
            _handle = new LoggerHandle(fileWriter, stream, consoleOut, consoleError);
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Full-stack logging initialized at {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unable to initialize full-stack file logging in '{directory}': {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => first.Encoding;

        public override void Write(char value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void Write(string? value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void WriteLine(string? value)
        {
            first.WriteLine(value);
            second.WriteLine(value);
        }

        public override void Flush()
        {
            first.Flush();
            second.Flush();
        }
    }

    private sealed class LoggerHandle(StreamWriter fileWriter, FileStream stream, TextWriter originalOut, TextWriter originalError) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
            catch
            {
                // Best-effort reset.
            }

            fileWriter.Dispose();
            stream.Dispose();
        }
    }
}
