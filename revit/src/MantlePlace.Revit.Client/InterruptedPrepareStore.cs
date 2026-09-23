using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>What <see cref="PrepareWatcher"/> tells the record as a Prepare starts and ends.</summary>
/// <remarks>
/// The seam exists so that the watcher is asserted without a disk (<c>HPS-42</c>). The real one is
/// <see cref="InterruptedPrepareStore"/>. Neither call may throw: a record that cannot be written
/// costs a re-join, never a Prepare.
/// </remarks>
public interface IPrepareLedger
{
    /// <summary>A Prepare of <paramref name="orderId"/>, asked for at <paramref name="startedAt"/>, is being watched.</summary>
    void Started(string orderId, DateTimeOffset startedAt);

    /// <summary>The Prepare of <paramref name="orderId"/> ended this way.</summary>
    void Ended(string orderId, PrepareEnding ending);
}

/// <summary>This process, and whether the process that wrote an entry still runs.</summary>
public static class PrepareOwners
{
    /// <summary>This Revit process.</summary>
    public static PrepareOwner Current { get; } = Of(Process.GetCurrentProcess());

    /// <summary>
    /// Whether <paramref name="owner"/> still runs: a process of its id, started when it started.
    /// </summary>
    /// <remarks>
    /// A process of that id this user cannot inspect is not the owner: the owner was a Revit this
    /// user ran. Reading it as dead risks one order polled twice, which the platform answers as one
    /// job (<c>HPS-24</c>); reading it as live would lose the re-join for a week.
    /// </remarks>
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

/// <summary>
/// This machine's record of Prepares being watched, on disk, shared by every Revit process on it —
/// so that one Revit closed or crashed during is re-joined by the next (<see cref="InterruptedPrepares"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>A record that cannot be read or written does nothing, silently.</b> Unlike the announced-order
/// record, nothing here is worth a guess: a lost entry costs one re-join, and a claim made against a
/// record this process could not read might take a Prepare another Revit is still watching.
/// </para>
/// <para>
/// A file that is not a record — damaged, or emptied by a write that failed halfway — holds no
/// entries, which costs the re-joins it held and nothing else.
/// </para>
/// </remarks>
public sealed class InterruptedPrepareStore : IPrepareLedger
{
    private readonly MachineRecordFile _file;
    private readonly PrepareOwner _self;
    private readonly Func<PrepareOwner, bool> _isAlive;
    private readonly Func<string?> _email;
    private readonly object _gate = new();

    /// <param name="path">The record's file.</param>
    /// <param name="self">This process.</param>
    /// <param name="isAlive">Whether an owner's process still runs.</param>
    /// <param name="email">The signed-in address, or empty when the grant named none.</param>
    public InterruptedPrepareStore(string path, PrepareOwner self, Func<PrepareOwner, bool> isAlive, Func<string?> email)
    {
        _file = new MachineRecordFile(path);
        _self = self;
        _isAlive = isAlive;
        _email = email;
    }

    /// <summary>Where the record lives: beside the bundle cache, under the curator's local app data.</summary>
    public static string DefaultPath => MachineRecordFile.InLocalAppData("interrupted-prepares.json");

    /// <inheritdoc/>
    public void Started(string orderId, DateTimeOffset startedAt)
    {
        InterruptedPrepare entry = new(orderId, startedAt, AnnouncedOrders.AccountKey(_email()), _self);
        Change(record => InterruptedPrepares.Started(record, entry));
    }

    /// <inheritdoc/>
    public void Ended(string orderId, PrepareEnding ending)
    {
        if (!InterruptedPrepares.KeepsEntry(ending))
        {
            Drop(orderId);
        }
    }

    /// <summary>Removes this process's entry for <paramref name="orderId"/>: the order is not one to re-join.</summary>
    public void Drop(string orderId)
        => Change(record => InterruptedPrepares.Ended(record, orderId, _self));

    /// <summary>Takes the interrupted Prepares this process may re-join, and drops the stale ones.</summary>
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

    /// <summary>One entry, or <c>null</c> for one missing a field: an entry that cannot be re-joined is no entry.</summary>
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
