using System;
using System.Collections.Generic;
using System.Reflection;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Platform;

/// <summary>
/// Windows Update Agent through late-bound COM (Microsoft.Update.Session). Search runs with Online = false, i.e. against the locally
/// cached catalogue only - it never contacts Microsoft or WSUS, never downloads or installs anything.
/// </summary>
public sealed class WindowsUpdateClient : IWindowsUpdateClient
{
    public WindowsUpdateInfo Read(int historyCount)
    {
        var info = new WindowsUpdateInfo { PendingTitles = new List<string>(), History = new List<UpdateHistoryEntry>() };
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null) { info.Error = "Windows Update Agent not available"; return info; }
            object session = Activator.CreateInstance(sessionType);
            object searcher = Get(session, "CreateUpdateSearcher");
            Set(searcher, "Online", false);
            try
            {
                object result = Call(searcher, "Search", "IsInstalled=0 and IsHidden=0");
                object updates = Get(result, "Updates");
                int count = (int)Get(updates, "Count");
                for (int i = 0; i < count && i < 100; i++)
                {
                    object u = Call(updates, "Item", i);
                    info.PendingTitles.Add(Convert.ToString(Get(u, "Title")));
                }
                info.Available = true;
            }
            catch (Exception ex) { info.Error = "pending search: " + ex.Message; }

            try
            {
                int total = (int)Call(searcher, "GetTotalHistoryCount");
                if (total > 0)
                {
                    object history = Call(searcher, "QueryHistory", 0, Math.Min(total, Math.Max(1, historyCount)));
                    int n = (int)Get(history, "Count");
                    for (int i = 0; i < n; i++)
                    {
                        object h = Call(history, "Item", i);
                        var date = (DateTime)Get(h, "Date");
                        int rc = (int)Get(h, "ResultCode");
                        info.History.Add(new UpdateHistoryEntry
                        {
                            Title = Convert.ToString(Get(h, "Title")),
                            DateUtc = date.Kind == DateTimeKind.Utc ? date : DateTime.SpecifyKind(date, DateTimeKind.Utc),   // WUA reports UTC
                            Result = rc == 2 ? "Succeeded" : rc == 3 ? "SucceededWithErrors" : rc == 4 ? "Failed" : rc == 5 ? "Aborted" : rc == 1 ? "InProgress" : "NotStarted",
                            HResult = "0x" + ((int)Get(h, "HResult")).ToString("X8")
                        });
                    }
                    info.Available = true;
                }
            }
            catch (Exception ex) { info.Error = (info.Error == null ? "" : info.Error + "; ") + "history: " + ex.Message; }
        }
        catch (Exception ex) { info.Error = ex.Message; }
        return info;
    }

    private static object Get(object o, string name) { return o.GetType().InvokeMember(name, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, o, null); }
    private static void Set(object o, string name, object v) { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { v }); }
    private static object Call(object o, string name, params object[] args) { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
}
