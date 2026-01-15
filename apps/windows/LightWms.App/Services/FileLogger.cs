using System.IO;

namespace LightWms.App;

public sealed class FileLogger
{
    private readonly string _path;
    private readonly object _sync = new();

    public FileLogger(string path)
    {
        _path = path;
    }

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Warn(string message)
    {
        Write("WARN", message);
    }

    public void Error(string message)
    {
        Write("ERROR", message);
    }

    public void Error(string message, Exception ex)
    {
        Write("ERROR", $"{message} :: {ex}");
    }

    private void Write(string level, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = $"{DateTime.Now:O} {level} {message}";
            lock (_sync)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best effort logging only.
        }
    }
}
