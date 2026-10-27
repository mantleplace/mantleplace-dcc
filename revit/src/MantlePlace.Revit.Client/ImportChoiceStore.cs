using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// This machine's record of what the curator ticked at their last Import, on disk, shared by every
/// Revit process on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per machine, not per Revit version</b> (<see cref="MachineRecordFile"/>), and the same
/// whichever path opened the window: a local zip and the vault read and write this one record.
/// </para>
/// <para>
/// ⚠ <b>Nothing here can fail an import.</b> A file that is missing, locked, unreadable or of another
/// shape reads as no remembered choice, and a write the folder refuses is dropped. The window then
/// opens on its defaults, which is where it opened before there was a memory.
/// </para>
/// </remarks>
public sealed class ImportChoiceStore
{
    private readonly string _path;
    private readonly MachineRecordFile _file;

    public ImportChoiceStore(string path)
    {
        _file = new MachineRecordFile(path);
        _path = path;
    }

    /// <summary>Where the record lives: beside the bundle cache, under the curator's local app data.</summary>
    public static string DefaultPath => MachineRecordFile.InLocalAppData("import-choice.json");

    /// <summary>The last Import's choice, or <c>null</c> when there is none to use.</summary>
    public RememberedChoice? Read()
    {
        try
        {
            // A file that is not there is not created by looking for it.
            return File.Exists(_path)
                ? _file.Change(bytes => (bytes, RememberedChoice.Parse(bytes)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remembers <paramref name="choice"/>, replacing the last one. Returns whether it was written.</summary>
    public bool Write(RememberedChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        try
        {
            byte[] bytes = choice.ToBytes();
            return _file.Change(_ => (bytes, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
