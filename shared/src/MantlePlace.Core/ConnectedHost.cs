using System.Text.Json;

namespace MantlePlace.Core;

public readonly record struct ConnectedHost(
    int Protocol,
    string Host,
    string HostVersion,
    string PluginVersion,
    string? Document,
    string Machine,
    string ReadsMin,
    int ReadsMaxMajor)
{
    public string ToJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol", Protocol);
            writer.WriteString("host", Host);
            writer.WriteString("hostVersion", HostVersion);
            writer.WriteString("pluginVersion", PluginVersion);
            if (Document is null)
            {
                writer.WriteNull("document");
            }
            else
            {
                writer.WriteString("document", Document);
            }

            writer.WriteString("machine", Machine);
            writer.WriteStartObject("reads");
            writer.WriteString("min", ReadsMin);
            writer.WriteNumber("maxMajor", ReadsMaxMajor);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

public static class ConnectedHosts
{
    public const int Protocol = 1;

    public static string HostId => HostIdentity.Id;

    public static ConnectedHost Announce(
        string hostVersion,
        string pluginVersion,
        string? document,
        string machine)
        => new(
            Protocol,
            HostId,
            hostVersion ?? string.Empty,
            pluginVersion ?? string.Empty,
            string.IsNullOrWhiteSpace(document) ? null : document,
            machine ?? string.Empty,
            ManifestVersions.MinSupportedManifestVersion,
            HostIdentity.NewestImportedMajor);
}

public sealed class ConnectionIdSource
{
    public static string Prefix => HostIdentity.Id + "-";

    private static readonly Lazy<ConnectionIdSource> Process = new(() => new ConnectionIdSource());

    public ConnectionIdSource()
    {
        Id = Prefix + Guid.NewGuid().ToString("N");
    }

    public string Id { get; }

    public static ConnectionIdSource ThisLaunch => Process.Value;
}
