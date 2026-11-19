using System;
using System.Collections.Generic;
using System.Linq;

namespace Agent.Verify;

public static class Program
{
    public static int Main(string[] args)
    {
        var t = new TestRunner();
        CoreTests.Register(t);
        ConfigTests.Register(t);
        LoggingTests.Register(t);
        SyncTests.Register(t);
        HardwareStorageNetworkTests.Register(t);
        return t.Run(args);
    }
}

public sealed class TestRunner
{
    private readonly List<KeyValuePair<string, Func<System.Threading.Tasks.Task>>> _tests = new List<KeyValuePair<string, Func<System.Threading.Tasks.Task>>>();
    public void Add(string name, Action a) { _tests.Add(new KeyValuePair<string, Func<System.Threading.Tasks.Task>>(name, () => { a(); return System.Threading.Tasks.Task.CompletedTask; })); }
    public void AddAsync(string name, Func<System.Threading.Tasks.Task> a) { _tests.Add(new KeyValuePair<string, Func<System.Threading.Tasks.Task>>(name, a)); }

    public int Run(string[] args)
    {
        int pass = 0, fail = 0;
        foreach (var t in _tests)
        {
            try { t.Value().GetAwaiter().GetResult(); pass++; Console.WriteLine("  PASS  " + t.Key); }
            catch (Exception ex) { fail++; Console.WriteLine("  FAIL  " + t.Key + "\n        " + ex.GetType().Name + ": " + ex.Message); }
        }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool c, string msg = "expected true") { if (!c) throw new Exception(msg); }
    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(what + " expected <" + expected + "> but was <" + actual + ">");
    }
}
