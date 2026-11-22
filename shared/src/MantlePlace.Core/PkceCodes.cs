using System.Security.Cryptography;
using System.Text;

namespace MantlePlace.Core;

public static class PkceCodes
{
    public const int VerifierEntropyBytes = 32;

    public const int VerifierEncodedLength = 43;

    public const string ChallengeMethod = "S256";

    public static string MakeCodeVerifier()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(VerifierEntropyBytes));

    public static string MakeCodeChallengeS256(string codeVerifier)
    {
        ArgumentNullException.ThrowIfNull(codeVerifier);
        return Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier)));
    }

    public static string MakeState()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(VerifierEntropyBytes));
}
