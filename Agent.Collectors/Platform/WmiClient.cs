using System;
using System.Collections.Generic;
using System.Management;
using Agent.Core;

namespace Agent.Collectors.Platform;

/// <summary>System.Management wrapper. Every query has a timeout; failures are returned as errors, never thrown to collectors.</summary>
public sealed class WmiClient : IWmiClient
{
    private readonly TimeSpan _timeout;

    public WmiClient(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public IReadOnlyList<IWmiObject> Query(string wql, string wmiNamespace = @"root\cimv2")
    {
        string error;
        return TryQuery(wql, wmiNamespace, out error);
    }

    public IReadOnlyList<IWmiObject> TryQuery(string wql, string wmiNamespace, out string error)
    {
        error = null;
        var list = new List<IWmiObject>();
        try
        {
            var scope = new ManagementScope(wmiNamespace, new ConnectionOptions { Timeout = _timeout, Impersonation = ImpersonationLevel.Impersonate });
            scope.Connect();
            var options = new EnumerationOptions { Timeout = _timeout, ReturnImmediately = true, Rewindable = false, BlockSize = 64 };
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject o in results)
                    list.Add(new WmiObject((ManagementObject)o));
            }
        }
        catch (ManagementException ex)
        {
            error = ex.ErrorCode + ": " + ex.Message;       // InvalidNamespace / InvalidClass / AccessDenied / ...
            return list;
        }
        catch (System.Runtime.InteropServices.COMException ex) { error = "COM 0x" + ex.ErrorCode.ToString("X8") + ": " + ex.Message; }
        catch (UnauthorizedAccessException ex) { error = "AccessDenied: " + ex.Message; }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        return list;
    }

    private sealed class WmiObject : IWmiObject
    {
        private readonly ManagementObject _o;
        public WmiObject(ManagementObject o) { _o = o; }

        public object this[string property]
        {
            get { try { return _o[property]; } catch (Exception) { return null; } }
        }

        public IReadOnlyList<IWmiObject> GetRelated(string resultClass)
        {
            var list = new List<IWmiObject>();
            try
            {
                using (var rs = _o.GetRelated(resultClass))
                    foreach (ManagementBaseObject r in rs) list.Add(new WmiObject((ManagementObject)r));
            }
            catch (Exception) { }
            return list;
        }
    }
}
