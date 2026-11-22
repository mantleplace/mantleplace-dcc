using System.Text.Json;
using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed class AnnouncedOrderStore
{
    private readonly MachineRecordFile _file;
    private readonly object _gate = new();

    private AnnouncedOrders? _last;

    public AnnouncedOrderStore(string path)
    {
        _file = new MachineRecordFile(path);
    }

    public static string DefaultPath => MachineRecordFile.InLocalAppData("announced-orders.json");

    public VaultNewsResult Claim(VaultListing listing, string? email)
        => Change(
            record =>
            {
                VaultNewsResult news = VaultNews.Arrivals(record, listing, email);
                return (news.Record, news);
            },
            unread: new VaultNewsResult([], AnnouncedOrders.Empty));

    public void Seen(VaultListing listing, string? email)
        => Change(record => (VaultNews.Seen(record, listing, email), true), unread: false);

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
