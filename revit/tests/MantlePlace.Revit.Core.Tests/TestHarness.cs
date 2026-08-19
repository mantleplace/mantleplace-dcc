using System.Globalization;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// A minimal assert-and-report harness. Collects every failure rather than stopping at the first,
/// so one run tells you everything that is wrong.
/// </summary>
internal sealed class TestRun
{
    private readonly List<string> _failures = [];
    private string _scope = string.Empty;
    private int _cases;

    internal int CaseCount => _cases;

    internal int FailureCount => _failures.Count;

    /// <summary>Runs one named case. An exception inside it is a failure, not a crash.</summary>
    internal void Case(string name, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        _cases++;
        _scope = name;
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Fail($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _scope = string.Empty;
        }
    }

    internal void Fail(string detail)
        => _failures.Add(string.IsNullOrEmpty(_scope) ? detail : $"[{_scope}] {detail}");

    internal void True(bool condition, string detail)
    {
        if (!condition)
        {
            Fail($"{detail} — expected true");
        }
    }

    internal void False(bool condition, string detail)
    {
        if (condition)
        {
            Fail($"{detail} — expected false");
        }
    }

    internal void Equal(string? actual, string? expected, string detail)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            Fail($"{detail} — expected \"{expected}\", got \"{actual}\"");
        }
    }

    internal void Equal(int actual, int expected, string detail)
    {
        if (actual != expected)
        {
            Fail($"{detail} — expected {expected}, got {actual}");
        }
    }

    internal void Equal(bool actual, bool expected, string detail)
    {
        if (actual != expected)
        {
            Fail($"{detail} — expected {expected}, got {actual}");
        }
    }

    internal void Within(double actual, double expected, double tolerance, string detail)
    {
        if (double.IsNaN(actual) || Math.Abs(actual - expected) > tolerance)
        {
            Fail(string.Format(
                CultureInfo.InvariantCulture,
                "{0} — expected {1} ±{2}, got {3}",
                detail,
                expected,
                tolerance,
                actual));
        }
    }

    internal void Contains(string? haystack, string needle, string detail)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
        {
            Fail($"{detail} — expected to contain \"{needle}\", got \"{haystack}\"");
        }
    }

    /// <summary>Prints the outcome and returns a process exit code.</summary>
    internal int Report(string suiteName)
    {
        if (_failures.Count == 0)
        {
            Console.WriteLine($"PASS [{suiteName}]: {_cases} case(s).");
            return 0;
        }

        Console.Error.WriteLine($"FAIL [{suiteName}]: {_failures.Count} failure(s) across {_cases} case(s).");
        foreach (string failure in _failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }

        return 1;
    }
}
