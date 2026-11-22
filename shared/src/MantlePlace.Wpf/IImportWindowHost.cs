using MantlePlace.Core;

namespace MantlePlace.Wpf;

public interface IImportWindowHost
{
    void Begin(ImportLayerChoice choice);

    void CancelRun();

    void Dismiss();

    void Note(string line);

    void Fault(Exception fault);

    void ChooseLevel(FidelityLevel level);
}
