using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.Revit.Client;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The modeless surface a browser sign-in runs behind: what is happening, and a way to stop it.
/// </summary>
/// <remarks>
/// <para>
/// It exists because <c>IExternalCommand.Execute</c> is synchronous and the sign-in is not. Waiting
/// for the browser round-trip on the calling thread froze the whole application for up to the
/// five-minute timeout, with nothing on screen and no way out. The command now starts the flow,
/// shows this, and returns; Revit stays usable while the curator is in their browser.
/// </para>
/// <para>
/// The Cancel button is the point, not decoration. <see cref="AuthSession.CancelSignIn"/> and the
/// cancellation token plumbing behind it have always existed; the ribbon command simply had no way
/// to reach them, so a sign-in that went wrong could only be waited out. The timeout is still there
/// underneath, but a backstop is not a substitute for a button.
/// </para>
/// <para>
/// One window per Revit session, enforced by the command. This is the shim: it starts the flow and
/// reports what came back, and every decision in between belongs to
/// <c>MantlePlace.Revit.Client</c> and the pure core beneath it.
/// </para>
/// </remarks>
internal sealed class SignInWindow : Window
{
    private readonly AuthSession _session;

    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Text = "Opening your browser. Finish signing in there, then come back — "
            + "Revit stays usable while you do.",
    };

    private readonly Button _cancel = new()
    {
        Content = "Cancel",
        Padding = new Thickness(12, 4, 12, 4),
        IsDefault = true,
    };

    private readonly Button _close = new()
    {
        Content = "Close",
        Padding = new Thickness(12, 4, 12, 4),
        Visibility = Visibility.Collapsed,
    };

    internal SignInWindow(AuthSession session, IntPtr revitWindow)
    {
        _session = session;

        Title = "Mantle Place — signing in";
        Width = 420;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Owned by Revit's main window, so it stays in front of it and minimises with it rather
        // than becoming a stray top-level window the curator loses behind the model.
        new WindowInteropHelper(this) { Owner = revitWindow };

        Content = BuildLayout();

        _cancel.Click += (_, _) => _session.CancelSignIn();
        _close.Click += (_, _) => Close();

        // Closing the window while the browser round-trip is still running cancels it. Unlike the
        // vault browser, where closing detaches from an import that should finish on its own, a
        // sign-in nobody is waiting for has nothing to finish for.
        Closed += (_, _) => _session.CancelSignIn();
    }

    private UIElement BuildLayout()
    {
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_close);

        DockPanel root = new() { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        return root;
    }

    /// <summary>
    /// Run the sign-in and report the outcome here rather than in a dialog nobody asked for.
    /// </summary>
    /// <remarks>
    /// Started with a discard by the command, so this must not let an exception escape: an
    /// unobserved fault on a fire-and-forget task surfaces later, somewhere unrelated, as a crash
    /// with no connection to the button that caused it.
    /// </remarks>
    internal async Task RunAsync()
    {
        try
        {
            // ConfigureAwait(true): resume on the WPF dispatcher, because everything after this
            // line touches the window.
            AuthOutcome outcome = await _session.SignInAsync().ConfigureAwait(true);

            if (outcome.Cancelled)
            {
                // ⛔HPS-09: timing out is a cancellation, not a failure. A curator who wandered off
                // comes back to a signed-out plugin, not an error they have to dismiss.
                Close();
                return;
            }

            if (!outcome.Succeeded)
            {
                Settle(outcome.Message.Length > 0
                    ? outcome.Message
                    : "Sign-in did not complete. Try again.");
                return;
            }

            Settle(Describe(_session));
        }
        catch (Exception ex)
        {
            Settle($"Sign-in did not complete: {ex.Message}");
        }
    }

    /// <summary>Show the final word and swap Cancel for Close — there is nothing left to cancel.</summary>
    private void Settle(string message)
    {
        _status.Text = message;
        _cancel.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Visible;
        _close.IsDefault = true;
    }

    /// <summary>Says plainly whether the session survives a restart (<c>HPS-16</c>).</summary>
    private static string Describe(AuthSession session)
    {
        string who = session.UserEmail.Length > 0
            ? $"Signed in as {session.UserEmail}."
            : "Signed in.";

        return session.IsPersistent
            ? who
            : who + " This machine has no secure credential store, so you will need to sign in again "
                + "next time Revit starts.";
    }
}
