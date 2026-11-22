using System.Text;
using System.Text.Json;
using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

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
        run.Equal(encoding.Str("unreserved")!, "A-Za-z0-9-._~", "the unreserved set the corpus states");
        run.Equal(
            UnescapedAscii(),
            "-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz~",
            "and the set this host's encoder actually leaves alone");

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

    private static void DriveBrowserPages(TestRun run, VectorNode root)
    {
        const string HostId = "revit";

        VectorNode copy = root.Obj("copy")!;
        VectorNode palette = root.Obj("palette")!;
        VectorNode structure = root.Obj("structure")!;
        VectorNode handOff = root.Obj("handOff")!;
        VectorNode escaping = root.Obj("escaping")!;

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

            foreach (VectorNode forbidden in structure.Items("bothPagesLack"))
            {
                string token = forbidden.AsString()!;
                run.False(page.Contains(token, StringComparison.Ordinal), $"{what}: no '{token}'");
            }

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
        run.Contains(success, doneUrl, "and the success page carries it");

        foreach (VectorNode required in handOff.Items("successPageContains"))
        {
            run.Contains(success, required.AsString()!, "the hand-off probes before it navigates");
        }

        foreach (VectorNode forbidden in handOff.Items("successPageLacks"))
        {
            string token = forbidden.AsString()!;
            run.False(success.Contains(token, StringComparison.Ordinal), $"the hand-off is not '{token}'");
        }

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

        string bodyPrefix = copy.Str("failureBodyPrefix")!;
        foreach (VectorNode character in escaping.Items("order"))
        {
            string raw = character.AsString()!;
            run.False(
                BrowserPages.Error(raw).Contains(bodyPrefix + raw + "<", StringComparison.Ordinal),
                $"'{raw}' never reaches the page raw");
        }

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

    private static void DriveRejectionClassification(TestRun run, VectorNode root)
    {
        int matrixStatus = root.Int("matrixStatus") ?? -1;

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
