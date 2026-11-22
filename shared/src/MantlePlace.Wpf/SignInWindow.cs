using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public sealed class SignInWindow : Window
{
    private readonly AuthSession _session;

    private bool _running;

    private readonly string _hostName = WindowLabels.HostName(HostIdentity.Id);

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    private readonly Button _cancel = new()
    {
        Content = WindowLabels.Cancel,
        Padding = new Thickness(12, 4, 12, 4),
        IsDefault = true,
    };

    private readonly Button _close = new()
    {
        Content = WindowLabels.Close,
        Padding = new Thickness(12, 4, 12, 4),
        Visibility = Visibility.Collapsed,
    };

    public SignInWindow(AuthSession session, IntPtr hostWindow)
    {
        _session = session;
        _status.Text = "Opening your browser. Finish signing in there, then come back — "
            + $"{_hostName} stays usable while you do.";

        Title = WindowLabels.SignInWindowTitle;
        BrandChrome.ShowMarkInTitleBar(this);
        Width = 420;

        Height = 190 + BrandChrome.HeaderHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        new WindowInteropHelper(this) { Owner = hostWindow };

        Content = BuildLayout();

        _cancel.Click += (_, _) => Guarded(() => _session.CancelSignIn());
        _close.Click += (_, _) => Guarded(Close);

        Closed += (_, _) => Guarded(() =>
        {
            if (_running)
            {
                _session.CancelSignIn();
            }
        });
    }

    private void Guarded(Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Settle($"Sign-in did not finish cleanly: {ex.Message}");
        }
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
        BrandChrome.AddHeader(root, WindowLabels.SignInHeading);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        return root;
    }

    public async Task RunAsync()
    {
        _running = true;
        try
        {
            AuthOutcome outcome = await _session.SignInAsync().ConfigureAwait(true);

            _running = false;

            if (outcome.Cancelled)
            {
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

            Settle(Describe(_session, _hostName));
        }
        catch (Exception ex)
        {
            Settle($"Sign-in did not complete: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    private void Settle(string message)
    {
        _status.Text = message;
        _cancel.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Visible;
        _close.IsDefault = true;
    }

    private static string Describe(AuthSession session, string hostName)
    {
        string who = session.UserEmail.Length > 0
            ? $"Signed in as {session.UserEmail}."
            : "Signed in.";

        return session.IsPersistent
            ? who
            : who + " This machine has no secure credential store, so you will need to sign in again "
                + $"next time {hostName} starts.";
    }
}
