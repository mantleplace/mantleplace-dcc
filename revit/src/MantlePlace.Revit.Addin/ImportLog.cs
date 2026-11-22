using System.IO;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed class ImportLog(string zipPath)
{
    private readonly string _path = LocalBundleSource.LogPathFor(zipPath);
    private readonly string _zipPath = zipPath;
    private bool _begun;

    private void Begin()
    {
        if (_begun)
        {
            return;
        }

        _begun = true;
        try
        {
            File.WriteAllText(
                _path,
                $"Mantle Place {PluginVersion.Current} bundle import, "
                    + $"started {DateTime.Now:yyyy-MM-dd HH:mm:ss}."
                    + Environment.NewLine
                    + _zipPath
                    + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal void Append(string line)
    {
        Begin();
        try
        {
            File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal void AppendBlock(string block)
    {
        Begin();
        try
        {
            File.AppendAllText(_path, Environment.NewLine + block + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal string AndSay(string message)
    {
        Append(message);
        return message;
    }
}
