using System.Text;
using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Drives the shared corpus' <c>auth</c> group (<c>HPS-40</c>, <c>HPS-41</c>).
/// </summary>
/// <remarks>
/// All five cases are <c>expect: "vector"</c>, so there are no <c>expectations</c> keys for
/// <c>HPS-46</c>'s asserted-keys check to bind. <see cref="VectorDocument"/> applies the same idea
/// one level down: every value in every file must be read by something, or the case fails with the
/// path. Without it a suite could drive row 0 of the state-validation table and skip row 3 — the
/// empty-expected-state row, which is the entire reason ⛔<c>HPS-07</c> exists — and still report
/// the case as covered.
/// </remarks>
internal static class AuthConformanceTests
{
    internal static int Run()
    {
        TestRun run = new();

        if (ConformanceCorpus.LoadGroup("auth", out List<ConformanceCorpus.CorpusCase> cases) is { } problem)
        {
            run.Fail(problem);
            return run.Report("auth conformance");
        }

        HashSet<string> driven = new(StringComparer.Ordinal);

        foreach (ConformanceCorpus.CorpusCase corpusCase in cases)
        {
            run.Case(corpusCase.Id, () =>
            {
                using VectorDocument vectors = VectorDocument.Parse(corpusCase.Payload);

                switch (corpusCase.Id)
                {
                    case "auth.pkceVectors":
                        DrivePkce(run, vectors.Root);
                        break;
                    case "auth.callbackQueryVectors":
                        DriveCallbackQuery(run, vectors.Root);
                        break;
                    case "auth.tokenResponseVectors":
                        DriveTokenResponses(run, vectors.Root);
                        break;
                    case "auth.rejectionClassification":
                        DriveRejectionClassification(run, vectors.Root);
                        break;
                    case "auth.stateMachine":
                        DriveStateMachine(run, vectors.Root);
                        break;
                    case "auth.browserPageVectors":
                        DriveBrowserPages(run, vectors.Root);
                        break;
                    default:
                        run.Fail($"no driver for corpus case '{corpusCase.Id}' — a case added upstream that "
                            + "nothing here asserts (HPS-41)");
                        return;
                }

                driven.Add(corpusCase.Id);

                foreach (string unread in vectors.UnreadPaths())
                {
                    run.Fail($"'{unread}' is a vector value nothing in this suite read. Assert it, or — if it "
                        + "is prose for a human — say so upstream so the reader can exempt it.");
                }
            });
        }

        foreach (string undriven in ConformanceCorpus.UndrivenCases(cases, driven))
        {
            run.Fail($"corpus case '{undriven}' loaded but nothing drove it (HPS-41)");
        }

        return run.Report("auth conformance");
    }

    private static void DrivePkce(TestRun run, VectorNode root)
    {
        VectorNode base64 = root.Obj("base64url")!;
        foreach (VectorNode vector in base64.Items("vectors"))
        {
            string hex = vector.Str("bytesHex")!;
            string expected = vector.Str("encoded")!;
            run.Equal(Base64Url.Encode(Convert.FromHexString(hex)), expected, $"base64url({hex})");
        }

        VectorNode verifier = root.Obj("verifier")!;
        run.Equal(verifier.Int("entropyBytes") ?? -1, PkceCodes.VerifierEntropyBytes, "verifier entropy");
        run.Equal(verifier.Int("encodedLength") ?? -1, PkceCodes.VerifierEncodedLength, "verifier encoded length");

        string minted = PkceCodes.MakeCodeVerifier();
        run.Equal(minted.Length, PkceCodes.VerifierEncodedLength, "a minted verifier is 43 characters");
        foreach (VectorNode forbidden in verifier.Items("mustNotContain"))
        {
            string character = forbidden.AsString()!;

            // An encoder that leaves these in place produces a verifier the server rejects only at
            // the exchange step, long after the flow looks correct.
            run.False(
                minted.Contains(character, StringComparison.Ordinal),
                $"a minted verifier contains no '{character}'");
        }

        VectorNode appendixB = root.Obj("challengeS256")!.Obj("rfc7636AppendixB")!;
        run.Equal(
            PkceCodes.MakeCodeChallengeS256(appendixB.Str("verifier")!),
            appendixB.Str("challenge")!,
            "RFC 7636 Appendix B S256 pair");

        VectorNode redirect = root.Obj("redirectUri")!;
        string built = AuthUrls.BuildLoopbackRedirectUri(51000, "/callback");
        run.Equal(
            redirect.Str("template")!.Replace("{port}", "51000", StringComparison.Ordinal)
                .Replace("{path}", "/callback", StringComparison.Ordinal),
            built,
            "the template, substituted, is what this host builds");
        run.Equal(built, redirect.Str("example")!, "redirect URI example");

        VectorNode encoding = root.Obj("percentEncoding")!;
        run.Equal(
            AuthUrls.PercentEncode(encoding.Str("example")!),
            encoding.Str("encoded")!,
            "percent-encoded redirect URI");
        // Two assertions, because the corpus states the set as a character-class shorthand. The
        // first pins the shorthand; the second is the behaviour, swept over printable ASCII and
        // listed in code-point order. Together they say "this encoder implements that set" without
        // a range parser nobody wants to maintain.
        run.Equal(encoding.Str("unreserved")!, "A-Za-z0-9-._~", "the unreserved set the corpus states");
        run.Equal(
            UnescapedAscii(),
            "-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz~",
            "and the set this host's encoder actually leaves alone");

        // The query order is pinned so two hosts produce byte-identical authorize URLs, which is
        // what makes a captured URL from one a usable reproduction for the other.
        string authorizeUrl = AuthUrls.BuildAuthorizeUrl("https://mantle.place/auth/native", built, "chal", "st");
        int cursor = 0;
        foreach (VectorNode parameter in root.Items("authorizeQueryOrder"))
        {
            string fragment = parameter.AsString()!;
            int at = authorizeUrl.IndexOf(fragment, cursor, StringComparison.Ordinal);
            run.True(at >= 0, $"authorize URL carries '{fragment}' after the previous parameter");
            cursor = at < 0 ? cursor : at + fragment.Length;
        }
    }

    /// <summary>
    /// The two pages the browser is shown (<c>HPS-08</c>), against the copy and structure the
    /// corpus pins.
    /// </summary>
    /// <remarks>
    /// The point of driving this from the corpus rather than from literals here: the two hosts
    /// render the same moment in the same flow, and they had already drifted into two headings and
    /// two sentences with nothing failing. Asserting a literal in each host's own suite reproduces
    /// exactly that — both suites stay green while disagreeing. Against one fixture they cannot.
    /// </remarks>
    private static void DriveBrowserPages(TestRun run, VectorNode root)
    {
        const string HostId = "revit";

        VectorNode copy = root.Obj("copy")!;
        VectorNode palette = root.Obj("palette")!;
        VectorNode structure = root.Obj("structure")!;
        VectorNode handOff = root.Obj("handOff")!;
        VectorNode escaping = root.Obj("escaping")!;

        // The whole host-name map is read, not just this host's row, so a third host added upstream
        // turns red here rather than being silently absent.
        VectorNode hostNames = copy.Obj("hostNames")!;
        string thisHost = string.Empty;
        foreach (string id in hostNames.Keys())
        {
            string name = hostNames.Str(id)!;
            run.True(name.Length > 0, $"the corpus names host '{id}'");
            if (id == HostId)
            {
                thisHost = name;
            }
        }

        run.True(thisHost.Length > 0, $"and names this one ({HostId})");
        string closing = copy.Str("closing")!.Replace("{hostName}", thisHost, StringComparison.Ordinal);

        string success = BrowserPages.Success();
        string failure = BrowserPages.Error("the server said no");

        foreach ((string what, string page) in new[] { ("success", success), ("failure", failure) })
        {
            foreach (VectorNode required in structure.Items("bothPagesContain"))
            {
                run.Contains(page, required.AsString()!, $"{what}: the document declares it");
            }

            // HPS-08 as the absence of every way to reach off-page for something the renderer NEEDS.
            // The hand-off below is not one of these: it is a navigation the page survives failing.
            foreach (VectorNode forbidden in structure.Items("bothPagesLack"))
            {
                string token = forbidden.AsString()!;
                run.False(page.Contains(token, StringComparison.Ordinal), $"{what}: no '{token}'");
            }

            // Whole declarations, not bare colours: a page that ships the value in a comment and
            // renders default white would pass the looser check.
            run.Contains(page, palette.Str("groundDeclaration")!, $"{what}: the brand ground");
            run.Contains(page, palette.Str("accentDeclaration")!, $"{what}: the brand accent");
            run.Contains(page, palette.Str("fontStackHead")!, $"{what}: a system face, never a webfont");

            run.Contains(page, copy.Str("wordmark")!, $"{what}: the wordmark");
            run.Contains(page, copy.Str("titleSuffix")!, $"{what}: the tab title carries the brand");
            run.Contains(page, closing, $"{what}: tells the user to close the tab");
        }

        run.Contains(success, copy.Str("successTitle")!, "the success heading");
        run.Contains(success, copy.Str("successBody")!, "and its sentence");
        run.Contains(failure, copy.Str("failureTitle")!, "the failure heading");
        run.Contains(failure, copy.Str("failureBodyPrefix")!, "and its sentence");

        string doneUrl = handOff.Str("urlTemplate")!.Replace("{hostId}", HostId, StringComparison.Ordinal);
        run.Equal(BrowserPages.DoneUrl, doneUrl, "the hand-off target the corpus states");
        // And that the page actually carries it. Asserting the constant alone would stay green if
        // someone edited the URL inside the script markup without touching the constant.
        run.Contains(success, doneUrl, "and the success page carries it");

        foreach (VectorNode required in handOff.Items("successPageContains"))
        {
            run.Contains(success, required.AsString()!, "the hand-off probes before it navigates");
        }

        // A meta refresh would navigate whether or not the done page answered, so an offline curator
        // would land on the browser's own error page and the branded page would have been for
        // nothing — in exactly the case it exists for.
        foreach (VectorNode forbidden in handOff.Items("successPageLacks"))
        {
            string token = forbidden.AsString()!;
            run.False(success.Contains(token, StringComparison.Ordinal), $"the hand-off is not '{token}'");
        }

        // successOnly drives behaviour rather than restating itself: when the corpus says only
        // success hands off, the failure page must not carry the hand-off target. The reason it
        // must not is that the failure body interpolates an error_description the authorization
        // server wrote, and a page that executes nothing is a page where the escape pass is the
        // only thing that has to be right about that string.
        bool successOnly = handOff.Bool("successOnly") ?? false;
        run.True(successOnly, "the corpus says only success hands off");
        if (successOnly)
        {
            run.False(
                failure.Contains(doneUrl, StringComparison.Ordinal),
                "so the failure page does not carry the hand-off target");
        }

        foreach (VectorNode forbidden in handOff.Items("failurePageLacks"))
        {
            string token = forbidden.AsString()!;
            run.False(failure.Contains(token, StringComparison.Ordinal), $"failure carries no '{token}'");
        }

        // Stated as "the raw character does not survive", not "the escape changed something": the
        // page is what the user sees, and that is where the character must not appear bare.
        string bodyPrefix = copy.Str("failureBodyPrefix")!;
        foreach (VectorNode character in escaping.Items("order"))
        {
            string raw = character.AsString()!;
            run.False(
                BrowserPages.Error(raw).Contains(bodyPrefix + raw + "<", StringComparison.Ordinal),
                $"'{raw}' never reaches the page raw");
        }

        // The vectors are what pin the ORDER. Run '<' before '&' over the first of them and the '<'
        // just written comes back as &amp;lt; — a different string, so it fails.
        IReadOnlyList<VectorNode> vectors = escaping.Items("vectors");
        run.True(vectors.Count > 0, "states escaping vectors");
        foreach (VectorNode vector in vectors)
        {
            string raw = vector.Str("raw")!;
            run.Equal(BrowserPages.HtmlEscape(raw), vector.Str("escaped")!, $"escaping '{raw}'");
        }
    }

    private static void DriveCallbackQuery(TestRun run, VectorNode root)
    {
        foreach (VectorNode recognised in root.Items("recognisedKeys"))
        {
            string key = recognised.AsString()!;
            run.True(
                AuthCallbackQuery.TryParse(key + "=value", out _),
                $"'{key}' is a key this host recognises");
        }

        foreach (VectorNode vector in root.Items("vectors"))
        {
            string input = vector.Str("input")!;
            bool expectParsed = vector.Bool("parsed") ?? false;

            bool parsed = AuthCallbackQuery.TryParse(input, out AuthCallback callback);
            run.Equal(parsed, expectParsed, $"parse '{input}'");

            if (!parsed)
            {
                continue;
            }

            run.Equal(callback.Code, vector.Str("code") ?? string.Empty, $"code from '{input}'");
            run.Equal(callback.State, vector.Str("state") ?? string.Empty, $"state from '{input}'");
            run.Equal(callback.Error, vector.Str("error") ?? string.Empty, $"error from '{input}'");
            run.Equal(
                callback.ErrorDescription,
                vector.Str("errorDescription") ?? string.Empty,
                $"error description from '{input}'");
        }

        foreach (VectorNode row in root.Items("stateValidation"))
        {
            string expected = row.Str("expected")!;
            string received = row.Str("received")!;
            run.Equal(
                AuthCallbackQuery.IsStateValid(expected, received),
                row.Bool("valid") ?? false,
                $"state '{expected}' vs '{received}'");
        }
    }

    /// <summary>
    /// Drives <c>auth.rejectionClassification</c>: does a failed grant mean the stored
    /// credential is permanently dead, or only that the platform could not answer?
    /// </summary>
    /// <remarks>
    /// This was a host-local table on both sides, each carrying a comment asking the next
    /// person to keep it in step with the other host by hand. The codes are a platform
    /// contract, so the table belongs upstream: a host that starts honouring a fifth spelling
    /// now fails here instead of diverging quietly, which is the whole reason the corpus
    /// exists.
    /// </remarks>
    private static void DriveRejectionClassification(TestRun run, VectorNode root)
    {
        int matrixStatus = root.Int("matrixStatus") ?? -1;

        // Every key x code pair, rather than the diagonal each host happened to hand-write.
        foreach (VectorNode key in root.Items("codeKeys"))
        {
            string codeKey = key.AsString()!;
            foreach (VectorNode code in root.Items("definitiveCodes"))
            {
                string definitiveCode = code.AsString()!;
                string body = "{\"" + codeKey + "\":\"" + definitiveCode + "\"}";
                run.True(
                    TokenGrants.IsDefinitiveRejection(matrixStatus, body),
                    $"{matrixStatus} {body} is definitive");
            }
        }

        foreach (VectorNode vector in root.Items("vectors"))
        {
            bool raw = vector.Bool("raw") ?? false;
            string body = raw
                ? vector.Element.GetProperty("body").GetString() ?? string.Empty
                : vector.Element.GetProperty("body").GetRawText();
            vector.MarkConsumed("body");

            int status = vector.Int("status") ?? -1;
            run.Equal(
                TokenGrants.IsDefinitiveRejection(status, body),
                vector.Bool("definitive") ?? false,
                $"{status} {body}");
        }
    }

    private static void DriveTokenResponses(TestRun run, VectorNode root)
    {
        foreach (VectorNode vector in root.Items("vectors"))
        {
            bool raw = vector.Bool("raw") ?? false;
            string body = raw
                ? vector.Element.GetProperty("body").GetString() ?? string.Empty
                : vector.Element.GetProperty("body").GetRawText();
            vector.MarkConsumed("body");

            string? failure = TokenGrants.TryParse(body, out TokenGrant? grant);
            bool expectParsed = vector.Bool("parsed") ?? false;
            run.Equal(failure is null, expectParsed, $"parse {body}");

            if (!expectParsed)
            {
                run.Contains(failure, vector.Str("errorContains")!, "failure message");
                continue;
            }

            run.Equal(grant!.ExpiresInSeconds, vector.Int("expiresInSeconds") ?? -1, "expires_in");
            run.Equal(grant.AccessToken, vector.Str("accessToken")!, "access token");
            run.Equal(grant.RefreshToken, vector.Str("refreshToken")!, "refresh token");

            if (vector.Str("userId") is { } userId)
            {
                run.Equal(grant.UserId, userId, "user id");
            }
        }

        // The precedence is asserted by removing one field at a time from a body that carries them
        // all: whichever remains highest must be the message. Asserting only the full body would
        // pass for any implementation that happens to read the first field.
        List<string> precedence = [];
        foreach (VectorNode field in root.Items("errorPrecedence"))
        {
            precedence.Add(field.AsString()!);
        }

        for (int skip = 0; skip < precedence.Count; skip++)
        {
            Dictionary<string, string> body = [];
            for (int i = skip; i < precedence.Count; i++)
            {
                body[precedence[i]] = "value-of-" + precedence[i];
            }

            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(body));
            run.Equal(
                TokenGrants.DescribeError(document.RootElement),
                "value-of-" + precedence[skip],
                $"'{precedence[skip]}' wins once the fields above it are gone");
        }

        foreach (VectorNode row in root.Items("chooseRefreshToken"))
        {
            run.Equal(
                TokenGrants.ChooseRefreshToken(row.Str("new")!, row.Str("prior")!),
                row.Str("chosen")!,
                "chosen refresh token");
        }

        VectorNode expiry = root.Obj("isExpired")!;
        int skew = expiry.Int("defaultSkewSeconds") ?? -1;
        run.Equal(skew, TokenGrants.ExpirySkewSeconds, "expiry skew");

        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        run.True(
            TokenGrants.IsExpired(now, now.AddSeconds(skew)),
            "the boundary is inclusive — exactly one skew out counts as expired");
        run.False(
            TokenGrants.IsExpired(now, now.AddSeconds(skew + 1)),
            "one second beyond the skew is not expired");
        run.True(
            TokenGrants.IsExpired(now, now.AddSeconds(skew - 1)),
            "inside the skew is expired");
    }

    private static void DriveStateMachine(TestRun run, VectorNode root)
    {
        List<AuthState> states = [];
        foreach (VectorNode state in root.Items("states"))
        {
            string name = state.AsString()!;
            run.True(Enum.TryParse(name, out AuthState parsed), $"'{name}' is a state this host has");
            states.Add(parsed);
        }

        run.Equal(states.Count, Enum.GetValues<AuthState>().Length, "the corpus names every state and no more");
        run.Equal(root.Str("initial")!, AuthStateMachine.Initial.ToString(), "initial state");

        // Read the table as data, then evaluate it over EVERY (state x event) pair — 45 of them —
        // rather than only the 13 rows it spells out. The rows are the interesting cases; the
        // unmatched remainder is where the unchanged-by-default rule lives, and that is the rule
        // that stops an out-of-order callback corrupting a session.
        List<(string Signal, string From, string To)> table = [];
        HashSet<string> tableEvents = new(StringComparer.Ordinal);
        foreach (VectorNode transition in root.Items("transitions"))
        {
            string signal = transition.Str("event")!;
            tableEvents.Add(signal);
            table.Add((signal, transition.Str("from")!, transition.Str("to")!));
        }

        foreach (AuthEvent signal in Enum.GetValues<AuthEvent>())
        {
            run.True(
                tableEvents.Contains(signal.ToString()),
                $"the corpus table covers the '{signal}' event this host implements");
        }

        run.Equal(tableEvents.Count, Enum.GetValues<AuthEvent>().Length, "and names no event this host lacks");

        foreach (AuthState state in states)
        {
            foreach (AuthEvent signal in Enum.GetValues<AuthEvent>())
            {
                run.Equal(
                    AuthStateMachine.NextState(state, signal).ToString(),
                    Resolve(table, state, signal).ToString(),
                    $"{state} + {signal}");
            }
        }
    }

    /// <summary>First matching rule wins; <c>*</c> matches any state; <c>unchanged</c> is a no-op.</summary>
    private static AuthState Resolve(
        List<(string Signal, string From, string To)> table,
        AuthState state,
        AuthEvent signal)
    {
        string name = signal.ToString();
        foreach ((string ruleSignal, string from, string to) in table)
        {
            if (!string.Equals(ruleSignal, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(from, "*", StringComparison.Ordinal)
                && !string.Equals(from, state.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            return string.Equals(to, "unchanged", StringComparison.Ordinal)
                ? state
                : Enum.Parse<AuthState>(to);
        }

        return state;
    }

    /// <summary>Every printable ASCII character this host's percent-encoder leaves alone.</summary>
    private static string UnescapedAscii()
    {
        StringBuilder kept = new();
        for (char character = ' '; character <= '~'; character++)
        {
            string one = character.ToString();
            if (string.Equals(AuthUrls.PercentEncode(one), one, StringComparison.Ordinal))
            {
                kept.Append(character);
            }
        }

        return kept.ToString();
    }
}
