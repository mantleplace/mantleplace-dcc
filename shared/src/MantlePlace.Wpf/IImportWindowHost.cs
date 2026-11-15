using MantlePlace.Core;

namespace MantlePlace.Wpf;

/// <summary>
/// What a host implements to run its own import behind the shared import window: the window says
/// what the curator did, and the host runs the steps.
/// </summary>
/// <remarks>
/// <para>
/// The window decides nothing about the run (<see cref="ImportWindowState"/> decides the window), and
/// the host tells it about the run only through <see cref="ImportRunView"/>s posted to
/// <see cref="ImportWindowThread"/>. A host that stages its steps implements
/// <see cref="IImportStepRunner"/> for <see cref="StagedImport"/> and posts
/// <see cref="ImportRunView.Of"/>; one that runs its own way posts <see cref="ImportRunView.From"/>.
/// </para>
/// <para>
/// ⛔ <b>Every call arrives on the thread that opened the window</b>, posted there by
/// <see cref="ImportWindowThread"/>, never on the window's own: the host's API is single-threaded,
/// and the window's thread must never wait on the host's.
/// </para>
/// </remarks>
public interface IImportWindowHost
{
    /// <summary>
    /// Import was pressed, once, with the curator's choice. Not called for a window opened with no
    /// checklist: that host begins its run itself.
    /// </summary>
    void Begin(ImportLayerChoice choice);

    /// <summary>The curator asked the running import to stop, once; it stops at its next boundary.</summary>
    void CancelRun();

    /// <summary>The window went before Import was pressed, or cannot go on: the import is dropped.</summary>
    void Dismiss();

    /// <summary>A line for the import's log: something went wrong without stopping anything.</summary>
    void Note(string line);

    /// <summary>A fault of the window's, for the host's fault dialog.</summary>
    void Fault(Exception fault);
}
