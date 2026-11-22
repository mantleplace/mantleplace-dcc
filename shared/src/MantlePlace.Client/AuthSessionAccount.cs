using MantlePlace.Core;

namespace MantlePlace.Client;

public static class AuthSessionAccount
{
    public static AccountRibbonState AccountFace(this AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return AccountRibbon.For(session.State, session.UserEmail, session.CanRenewSession);
    }
}
