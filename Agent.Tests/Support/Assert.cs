using System.Collections.Generic;
using Xunit.Sdk;

namespace Agent.Tests;

/// <summary>
/// Tiny assertion facade over xUnit. It lives in the test namespace on purpose: inside Agent.Tests, "Assert" resolves to this
/// class, which keeps the (expected, actual, "what") call shape used throughout the tests and always produces readable messages.
/// </summary>
public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new XunitException(message);
    }

    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new XunitException((string.IsNullOrEmpty(what) ? "" : what + ": ") + "expected <" + expected + "> but was <" + actual + ">");
    }
}
