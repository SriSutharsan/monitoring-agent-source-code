using System;
using System.Reflection;
using Agent.Core;

namespace Agent.Collectors.Platform;

/// <summary>Windows Firewall profile state through the HNetCfg.FwPolicy2 COM object (late bound: no interop assembly needed, Vista+).</summary>
public sealed class FirewallPolicyReader : IFirewallPolicyReader
{
    public bool?[] ReadProfileStates(out string activeProfiles)
    {
        activeProfiles = null;
        object policy = null;
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t == null) return null;
            policy = Activator.CreateInstance(t);
            // NET_FW_PROFILE_TYPE2: Domain = 1, Private = 2, Public = 4
            var states = new bool?[3];
            int[] profiles = { 1, 2, 4 };
            for (int i = 0; i < 3; i++)
            {
                try { states[i] = (bool)t.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, new object[] { profiles[i] }); }
                catch (Exception) { states[i] = null; }
            }
            try
            {
                int current = (int)t.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null);
                var names = new System.Collections.Generic.List<string>();
                if ((current & 1) != 0) names.Add("domain"); if ((current & 2) != 0) names.Add("private"); if ((current & 4) != 0) names.Add("public");
                activeProfiles = string.Join(",", names.ToArray());
            }
            catch (Exception) { }
            return states;
        }
        catch (Exception) { return null; }
        finally { if (policy != null && System.Runtime.InteropServices.Marshal.IsComObject(policy)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(policy); }
    }
}
