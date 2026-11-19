using System;
using System.Collections.Generic;
using System.Diagnostics;
using Agent.Core;

namespace Agent.Collectors.Platform;

/// <summary>Process names, CPU time and memory only. Command lines, window titles and open files are deliberately not read.</summary>
public sealed class ProcessSource : IProcessSource
{
    public IReadOnlyList<RawProcess> Snapshot()
    {
        var list = new List<RawProcess>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0 || p.Id == 4) continue;           // Idle / System
                list.Add(new RawProcess { Pid = p.Id, Name = p.ProcessName, CpuTime = p.TotalProcessorTime, WorkingSetBytes = p.WorkingSet64, PrivateBytes = p.PrivateMemorySize64 });
            }
            catch (Exception) { }                                // access denied / exited: skip
            finally { p.Dispose(); }
        }
        return list;
    }
}
