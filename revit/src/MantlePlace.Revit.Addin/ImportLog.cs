// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The import's record on disk, written as the import happens rather than after it returns.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Both import entry points used to report only at the end — the ribbon command with one
/// <c>WriteAllText</c>, the vault window with a string handed back to the browser and no file at
/// all. So a run that hung, crashed, or was clicked away left nothing behind, and the one step
/// known to take minutes is exactly the step a session is most likely to die inside: the
/// site-boundary subdivisions spent over ten minutes in a single commit on a 79,806-point
/// toposolid. The last line in this file names whatever was in flight.
/// </para>
/// <para>
/// Best-effort by contract. An unwritable path must not turn a successful import into a failure,
/// and it must not turn a slow one into a crash — every method here swallows the two exceptions a
/// file sink actually raises and nothing else.
/// </para>
/// </remarks>
internal sealed class ImportLog(string zipPath)
{
    private readonly string _path = LocalBundleSource.LogPathFor(zipPath);
    private readonly string _zipPath = zipPath;

    /// <summary>Truncates the file, so one run's record can never be read as another's.</summary>
    internal void Begin()
    {
        try
        {
            File.WriteAllText(
                _path,
                $"Mantle Place bundle import, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}."
                    + Environment.NewLine
                    + _zipPath
                    + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One line, now.</summary>
    internal void Append(string line)
    {
        try
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes <paramref name="message"/> and hands it back, for the early returns that are both the
    /// caller's answer and the whole of the record.
    /// </summary>
    internal string AndSay(string message)
    {
        Append(message);
        return message;
    }
}
