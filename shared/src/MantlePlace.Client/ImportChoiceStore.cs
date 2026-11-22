using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed class ImportChoiceStore
{
    private readonly string _path;
    private readonly MachineRecordFile _file;

    public ImportChoiceStore(string path)
    {
        _file = new MachineRecordFile(path);
        _path = path;
    }

    public static string DefaultPath => MachineRecordFile.InLocalAppData("import-choice.json");

    public RememberedChoice? Read()
    {
        try
        {
            return File.Exists(_path)
                ? _file.Change(bytes => (bytes, RememberedChoice.Parse(bytes)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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
