using System.Diagnostics;
using System.Text.Json;

namespace RoslynMcp.Tests;

internal enum TestCategory
{
    Basic,
    Advanced,
    EdgeCase,
    FailureCase
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record TestCase(
    string MethodName,
    TestCategory Category,
    string Description,
    Func<Task> ExecuteAsync)
{
    public override string ToString() => $"[{MethodName} | {Category}] {Description}";
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record TestResult(
    string MethodName,
    TestCategory Category,
    string Description,
    bool Passed,
    long DurationMs,
    string? ErrorMessage = null)
{
    public override string ToString() =>
        Passed
            ? $"PASS [{MethodName} | {Category}] {Description} ({DurationMs}ms)"
            : $"FAIL [{MethodName} | {Category}] {Description} ({DurationMs}ms) -> {ErrorMessage}";
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record MethodCoverageReport(
    string MethodName,
    int TotalTests,
    int PassedTests,
    bool HasBasic,
    bool HasAdvanced,
    bool HasEdgeCase,
    bool HasFailureCase)
{
    public bool IsFullyCovered => HasBasic && HasAdvanced && HasEdgeCase && HasFailureCase && PassedTests == TotalTests;

    public override string ToString() =>
        $"{MethodName}: {PassedTests}/{TotalTests} passed (Basic={HasBasic}, Advanced={HasAdvanced}, Edge={HasEdgeCase}, Failure={HasFailureCase})";
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed record TestSuiteReport(
    int TotalMethods,
    int TotalTests,
    int PassedTests,
    int FailedTests,
    long TotalDurationMs,
    MethodCoverageReport[] MethodCoverage,
    TestResult[] Results)
{
    public bool AllPassed => FailedTests == 0 && MethodCoverage.All(m => m.IsFullyCovered);

    public override string ToString() =>
        $"TestSuiteReport(methods={TotalMethods}, passed={PassedTests}/{TotalTests}, failed={FailedTests}, duration={TotalDurationMs}ms, allPassed={AllPassed})";
}

internal static class AssertEx
{
    public static void True(bool condition, string message = "Expected condition to be true.")
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    public static void False(bool condition, string message = "Expected condition to be false.")
    {
        if (condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected '{expected}', but got '{actual}'.");
    }

    public static void Contains(string expectedSubstring, string actualText, string? message = null)
    {
        if (actualText is null || !actualText.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new InvalidOperationException(message ?? $"Expected string to contain '{expectedSubstring}', actual: '{actualText}'.");
    }

    public static void DoesNotContain(string forbiddenSubstring, string actualText, string? message = null)
    {
        if (actualText is not null && actualText.Contains(forbiddenSubstring, StringComparison.Ordinal))
            throw new InvalidOperationException(message ?? $"Expected string NOT to contain '{forbiddenSubstring}', actual: '{actualText}'.");
    }

    public static JsonElement ParseJson(string json)
    {
        True(!string.IsNullOrWhiteSpace(json), "Expected non-empty JSON string.");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static void Throws<TException>(Action action, string? messageContains = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            var actual = ex is System.Reflection.TargetInvocationException tie && tie.InnerException is not null
                ? tie.InnerException
                : ex;
            if (actual is not TException)
                throw new InvalidOperationException($"Expected exception of type {typeof(TException).Name}, but caught {actual.GetType().Name}: {actual.Message}");
            if (!string.IsNullOrEmpty(messageContains) && !actual.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Exception message '{actual.Message}' did not contain '{messageContains}'.");
            return;
        }
        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action, string? messageContains = null) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            var actual = ex is System.Reflection.TargetInvocationException tie && tie.InnerException is not null
                ? tie.InnerException
                : ex;
            if (actual is not TException)
                throw new InvalidOperationException($"Expected exception of type {typeof(TException).Name}, but caught {actual.GetType().Name}: {actual.Message}");
            if (!string.IsNullOrEmpty(messageContains) && !actual.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Exception message '{actual.Message}' did not contain '{messageContains}'.");
            return;
        }
        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }
}
