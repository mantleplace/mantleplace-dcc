namespace MantlePlace.Core;

/// <summary>
/// The UI Automation ids of the shared import window and its controls: what a live check finds them by,
/// in every host, whatever the window's words are in. Declared here, headless, so a check and a test can
/// name them without loading WPF.
/// </summary>
public static class ImportWindowAutomation
{
    /// <summary>The import window itself.</summary>
    public const string Window = "MantlePlace.ImportWindow";

    /// <summary>The Import button.</summary>
    public const string Import = Window + ".Import";

    /// <summary>The Cancel button.</summary>
    public const string Cancel = Window + ".Cancel";

    /// <summary>The Close button.</summary>
    public const string Close = Window + ".Close";

    /// <summary>The level control's list of levels (a combo box), shown for a 3.0 order that offers one.</summary>
    public const string Level = Window + ".Level";

    /// <summary>The line under the level control: what the last level choice did.</summary>
    public const string LevelNote = Window + ".LevelNote";

    /// <summary>The text of a stage before Import, for a host with no checklist that asked for one.</summary>
    public const string BeforeImport = Window + ".BeforeImport";

    /// <summary>The status line under the steps.</summary>
    public const string Status = Window + ".Status";

    /// <summary>The closing report.</summary>
    public const string Report = Window + ".Report";
}
