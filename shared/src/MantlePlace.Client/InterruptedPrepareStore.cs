using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MantlePlace.Core;

namespace MantlePlace.Client;

public interface IPrepareLedger
{
    void Started(string orderId, DateTimeOffset startedAt);

    void Ended(string orderId, PrepareEnding ending);
}

public static class PrepareOwners
{
    public static PrepareOwner Current { get; } = Of(Process.GetCurrentProcess());

    public static bool IsAlive(PrepareOwner owner)
    {
        try
        {
            using Process process = Process.GetProcessById(owner.ProcessId);
            return !process.HasExited && Of(process) == owner;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static PrepareOwner Of(Process process)
        => new(process.Id, process.StartTime.ToUniversalTime().Ticks);
}

public sealed class InterruptedPrepareStore : IPrepareLedger
{
    private readonly MachineRecordFile _file;
    private readonly PrepareOwner _self;
    private readonly Func<PrepareOwner, bool> _isAlive;
    private readonly Func<string?> _email;
    private readonly object _gate = new();

    public InterruptedPrepareStore(string path, PrepareOwner self, Func<PrepareOwner, bool> isAlive, Func<string?> email)
    {
        _file = new MachineRecordFile(path);
        _self = self;
        _isAlive = isAlive;
        _email = email;
    }

    public static string DefaultPath => MachineRecordFile.InLocalAppData("interrupted-prepares.json");

    public void Started(string orderId, DateTimeOffset startedAt)
    {
        InterruptedPrepare entry = new(orderId, startedAt, AnnouncedOrders.AccountKey(_email()), _self);
        Change(record => InterruptedPrepares.Started(record, entry));
    }

    public void Ended(string orderId, PrepareEnding ending)
    {
        if (!InterruptedPrepares.KeepsEntry(ending))
        {
            Drop(orderId);
        }
    }

    public void Drop(string orderId)
        => Change(record => InterruptedPrepares.Ended(record, orderId, _self));

    public IReadOnlyList<InterruptedPrepare> Claim(DateTimeOffset now)
    {
        string? account = AnnouncedOrders.AccountKey(_email());
        return Change(
            record =>
            {
                InterruptedPrepareClaim claim = InterruptedPrepares.Claim(record, now, account, _self, _isAlive);
                return (claim.Record, claim.Claimed);
            },
            unreadable: []);
    }

    private void Change(Func<IReadOnlyList<InterruptedPrepare>, IReadOnlyList<InterruptedPrepare>> change)
        => Change(record => (change(record), true), unreadable: false);

    private T Change<T>(Func<IReadOnlyList<InterruptedPrepare>, (IReadOnlyList<InterruptedPrepare> Record, T Result)> change, T unreadable)
    {
        lock (_gate)
        {
            try
            {
                return _file.Change(bytes =>
                {
                    (IReadOnlyList<InterruptedPrepare> changed, T answer) = change(Read(bytes));
                    return (Serialise(changed), answer);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return unreadable;
            }
        }
    }

    private static List<InterruptedPrepare> Read(byte[] bytes)
    {
        List<InterruptedPrepare> entries = [];
        if (bytes.Length == 0)
        {
            return entries;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("prepares", out JsonElement prepares)
                || prepares.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (JsonElement item in prepares.EnumerateArray())
            {
                if (Entry(item) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return entries;
    }

    private static InterruptedPrepare? Entry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("order", out JsonElement order) || order.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("startedAt", out JsonElement started) || started.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(started.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset startedAt)
            || !item.TryGetProperty("ownerProcess", out JsonElement process) || !process.TryGetInt32(out int processId)
            || !item.TryGetProperty("ownerStarted", out JsonElement ownerStarted) || !ownerStarted.TryGetInt64(out long ownerTicks)
            || string.IsNullOrWhiteSpace(order.GetString()))
        {
            return null;
        }

        string? account = item.TryGetProperty("account", out JsonElement held) && held.ValueKind == JsonValueKind.String
            ? held.GetString()
            : null;

        return new InterruptedPrepare(order.GetString()!, startedAt, account, new PrepareOwner(processId, ownerTicks));
    }

    private static byte[] Serialise(IReadOnlyList<InterruptedPrepare> record)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartArray("prepares");
            foreach (InterruptedPrepare entry in record)
            {
                json.WriteStartObject();
                json.WriteString("order", entry.OrderId);
                json.WriteString("startedAt", entry.StartedAt.ToString("O", CultureInfo.InvariantCulture));
                if (entry.Account is not null)
                {
                    json.WriteString("account", entry.Account);
                }

                json.WriteNumber("ownerProcess", entry.Owner.ProcessId);
                json.WriteNumber("ownerStarted", entry.Owner.StartedUtcTicks);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
