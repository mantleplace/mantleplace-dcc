using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The presence entry this plugin announces on the curator's private channel, one per Revit launch
/// (export protocol 1: the platform export contract, section 2).
/// </summary>
/// <param name="Protocol">The wire protocol this plugin speaks.</param>
/// <param name="Host">The host id; always <c>revit</c> here.</param>
/// <param name="HostVersion">The Revit version, as Revit states it.</param>
/// <param name="PluginVersion">This plugin's version.</param>
/// <param name="Document">The open document's title, or <c>null</c> when none is open.</param>
/// <param name="Machine">The machine name the curator sees beside the host.</param>
/// <param name="ReadsMin">The lowest bundle manifest version this plugin reads.</param>
/// <param name="ReadsMaxMajor">The newest bundle manifest major this plugin reads.</param>
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
    /// <summary>
    /// The presence payload as the channel carries it: camelCase keys, <c>document</c> present and
    /// <c>null</c> when no document is open, <c>reads</c> a nested object.
    /// </summary>
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

/// <summary>
/// Builds the presence payload, and owns the connection id (the platform export contract,
/// section 2).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>The manifest range is never written here.</b> <c>reads.min</c> is
/// <see cref="ManifestVersions.MinSupportedManifestVersion"/> and <c>reads.maxMajor</c> is
/// <see cref="ManifestVersions.NewestSupportedMajor"/>: the reader's own floor and newest major, so
/// the web greys out exactly the bundles this reader would refuse, and a range raised in one place
/// moves in both.
/// </para>
/// </remarks>
public static class ConnectedHosts
{
    /// <summary>The wire protocol this plugin speaks.</summary>
    public const int Protocol = 1;

    /// <summary>The host id the platform's contract names for this plugin.</summary>
    public const string HostId = "revit";

    /// <summary>The payload this plugin announces, for the document that is open (or none).</summary>
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
            ManifestVersions.NewestSupportedMajor);
}

/// <summary>
/// A connection id: random, made once when the plugin loads, never written anywhere.
/// </summary>
/// <remarks>
/// <para>
/// One per Revit launch — not per sign-in, and not kept across launches — so an export addressed to
/// a Revit that has since closed cannot find this one. It is the presence key and the only thing that
/// scopes an export to a plugin, so it is treated as visible to every signed-in program of the
/// curator, never as a secret.
/// </para>
/// <para>
/// A source holds one id for its lifetime; <see cref="ThisLaunch"/> is the process's source. Tests
/// make their own, which is how "stable within a launch, fresh across launches" is asserted.
/// </para>
/// </remarks>
public sealed class ConnectionIdSource
{
    /// <summary>The contract bounds an id to 1 to 200 characters; this one is far inside it.</summary>
    public const string Prefix = "revit-";

    private static readonly Lazy<ConnectionIdSource> Process = new(() => new ConnectionIdSource());

    public ConnectionIdSource()
    {
        Id = Prefix + Guid.NewGuid().ToString("N");
    }

    /// <summary>The id of this source, the same on every read.</summary>
    public string Id { get; }

    /// <summary>The process's source: its <see cref="Id"/> is this Revit launch's connection id.</summary>
    public static ConnectionIdSource ThisLaunch => Process.Value;
}
