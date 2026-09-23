using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// This machine's record of announced orders, on disk, shared by every Revit process on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file per machine, not per Revit version</b> (<see cref="MachineRecordFile"/>). Revit 2025
/// and 2027 open side by side list the same vault; with a record each, both would announce every new
/// order. The first process to claim an order announces it and the other reads it back already
/// announced.
/// </para>
/// <para>
/// ⚠ <b>A record that cannot be written still announces.</b> A duplicate notice is a smaller failure
/// than a lost one. When the file stays locked, or the folder refuses the write, the decision is made
/// against what this process last read, held in memory, so the process still does not repeat itself.
/// A process that has never read the record cannot tell news from a first listing, so it claims
/// nothing and leaves the orders unannounced for the next listing that can.
/// </para>
/// <para>
/// A file that is not a record — damaged, or emptied by a write that failed halfway — is a machine
/// that has never listed: its next listing is taken as seen, which costs at most one listing's news,
/// never a flood.
/// </para>
/// </remarks>
public sealed class AnnouncedOrderStore
{
    private readonly MachineRecordFile _file;
    private readonly object _gate = new();

    /// <summary>What this process last read or wrote; <c>null</c> until it has read the record once.</summary>
    private AnnouncedOrders? _last;

    public AnnouncedOrderStore(string path)
    {
        _file = new MachineRecordFile(path);
    }

    /// <summary>Where the record lives: beside the bundle cache, under the curator's local app data.</summary>
    public static string DefaultPath => MachineRecordFile.InLocalAppData("announced-orders.json");

    /// <summary>Claims the unannounced orders in <paramref name="listing"/> for this process.</summary>
    public VaultNewsResult Claim(VaultListing listing, string? email)
        => Change(
            record =>
            {
                VaultNewsResult news = VaultNews.Arrivals(record, listing, email);
                return (news.Record, news);
            },
            unread: new VaultNewsResult([], AnnouncedOrders.Empty));

    /// <summary>Records every order the curator saw listed in the vault browser.</summary>
    public void Seen(VaultListing listing, string? email)
        => Change(record => (VaultNews.Seen(record, listing, email), true), unread: false);

    /// <summary>Records an order another notice has told the curator about.</summary>
    public void Announce(string orderId) => Change(record => (record.Announcing(orderId), true), unread: false);

    private T Change<T>(Func<AnnouncedOrders, (AnnouncedOrders Record, T Result)> change, T unread)
    {
        lock (_gate)
        {
            try
            {
                (AnnouncedOrders after, T result) = _file.Change(bytes =>
                {
                    (AnnouncedOrders changed, T answer) = change(Read(bytes));
                    return (Serialise(changed), (changed, answer));
                });
                _last = after;
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_last is null)
                {
                    return unread;
                }

                (AnnouncedOrders after, T result) = change(_last);
                _last = after;
                return result;
            }
        }
    }

    private static AnnouncedOrders Read(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return AnnouncedOrders.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return AnnouncedOrders.Empty;
            }

            return new AnnouncedOrders(
                Strings(root, "orders"),
                Strings(root, "accounts"),
                root.TryGetProperty("machineListed", out JsonElement listed) && listed.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return AnnouncedOrders.Empty;
        }
    }

    private static IEnumerable<string> Strings(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement array)
            && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];

    private static byte[] Serialise(AnnouncedOrders record)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteBoolean("machineListed", record.MachineListed);
            json.WriteStartArray("orders");
            foreach (string order in record.Orders.Order(StringComparer.Ordinal))
            {
                json.WriteStringValue(order);
            }

            json.WriteEndArray();
            json.WriteStartArray("accounts");
            foreach (string account in record.Accounts.Order(StringComparer.Ordinal))
            {
                json.WriteStringValue(account);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
