using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>Reads an <see cref="AuthSession"/> as the Account button sees it.</summary>
/// <remarks>
/// <para>
/// One line, and it earns its place by being the only one: <see cref="AccountRibbon.For"/> takes
/// three values that always travel together off the same session, and every caller that assembled
/// them by hand was a caller that could assemble them wrongly — or miss the fourth if one is ever
/// added.
/// </para>
/// <para>
/// An extension in <c>Client</c> rather than a member of either side, because the pure core may not
/// reference a session that holds HTTP and a secret store, and the session should not grow an
/// opinion about a ribbon.
/// </para>
/// </remarks>
public static class AuthSessionAccount
{
    /// <summary>The Account split button's face and dropdown for this session, right now.</summary>
    public static AccountRibbonState AccountFace(this AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return AccountRibbon.For(session.State, session.UserEmail, session.CanRenewSession);
    }
}
