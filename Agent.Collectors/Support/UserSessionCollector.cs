using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Collectors.Events;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Collectors.Support;

public class UserSessionBatch
{
    public List<UserSessionEvent> Events { get; set; }
}

/// <summary>
/// Tracks user logon, logoff, screen lock, and unlock events from the Windows Security Log and Terminal Services.
/// Used for employee work hours, attendance auditing, and active presence tracking.
/// </summary>
public sealed class UserSessionCollector : CollectorBase<UserSessionBatch>
{
    private const string CursorKey = "sessions.cursor.Security";
    private readonly IEventLogReader _reader;
    private readonly INativeSystem _native;
    private readonly IStateStore _state;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private string _lastActiveUser;
    private string _lastSessionState;

    public UserSessionCollector(IEventLogReader reader, INativeSystem native, IStateStore state, IClock clock, ILogger<UserSessionCollector> log)
    {
        _reader = reader;
        _native = native;
        _state = state;
        _clock = clock;
        _log = log;
    }

    public override string Category => Categories.Sessions;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public override Task<UserSessionBatch> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect(ctx.DeviceId, ct));
    }

    public UserSessionBatch Collect(string deviceId, CancellationToken ct)
    {
        var list = new List<UserSessionEvent>();
        var now = _clock.UtcNow;

        // 1. Read Windows Security Log (Event IDs: 4624=Logon, 4634/4647=Logoff, 4800=Lock, 4801=Unlock)
        long cursor = 0;
        long.TryParse(_state.Get(CursorKey), out cursor);

        var spec = new EventQuerySpec
        {
            LogName = "Security",
            MinRecordIdExclusive = cursor,
            SinceUtc = cursor == 0 ? now.AddHours(-12) : (DateTime?)null,
            Predicate = "System[(EventID=4624 or EventID=4634 or EventID=4647 or EventID=4800 or EventID=4801)]",
            MaxEvents = 100
        };

        string error;
        var raw = _reader.Read(spec, out error);
        long maxSeen = cursor;

        if (raw != null && raw.Count > 0)
        {
            foreach (var r in raw.OrderBy(e => e.RecordId))
            {
                if (r.RecordId > maxSeen) maxSeen = r.RecordId;

                string eventType = null;
                string logonTypeStr = null;
                string username = EventCatalog.Get(r.Data, "TargetUserName", "SubjectUserName", "User");
                string domain = EventCatalog.Get(r.Data, "TargetDomainName", "SubjectDomainName", "Domain");

                // Skip machine accounts (ending in $) and system service identities
                if (string.IsNullOrEmpty(username) || username.EndsWith("$") ||
                    username.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                    username.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) ||
                    username.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) ||
                    username.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase) ||
                    username.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (r.EventId)
                {
                    case 4624: // Logon
                        var lt = EventCatalog.Get(r.Data, "LogonType");
                        if (lt == "7")
                        {
                            eventType = "unlock";
                            logonTypeStr = "Unlock";
                        }
                        else if (lt == "2" || lt == "10" || lt == "11")
                        {
                            eventType = "logon";
                            logonTypeStr = (lt == "10") ? "RemoteDesktop" : "Interactive";
                        }
                        break;

                    case 4634: // Logoff
                    case 4647: // User initiated logoff
                        eventType = "logoff";
                        break;

                    case 4800: // Workstation Locked
                        eventType = "lock";
                        break;

                    case 4801: // Workstation Unlocked
                        eventType = "unlock";
                        break;
                }

                if (eventType != null)
                {
                    list.Add(new UserSessionEvent
                    {
                        EventType = eventType,
                        Username = username,
                        Domain = domain,
                        SessionId = 1,
                        LogonType = logonTypeStr,
                        OccurredAtUtc = r.TimestampUtc,
                        Metadata = new Dictionary<string, string>
                        {
                            { "EventId", r.EventId.ToString(CultureInfo.InvariantCulture) },
                            { "RecordId", r.RecordId.ToString(CultureInfo.InvariantCulture) }
                        }
                    });
                }
            }

            if (maxSeen > cursor)
            {
                _state.Set(CursorKey, maxSeen.ToString(CultureInfo.InvariantCulture));
            }
        }

        // 2. Also check active interactive session state transition via WTS
        try
        {
            var sessions = _native.GetSessions();
            if (sessions != null)
            {
                var active = sessions.FirstOrDefault(s => s.State != null && s.State.Equals("active", StringComparison.OrdinalIgnoreCase));
                if (active != null && !string.IsNullOrEmpty(active.User))
                {
                    if (_lastActiveUser == null)
                    {
                        _lastActiveUser = active.User;
                        _lastSessionState = "active";
                        if (list.Count == 0 && cursor == 0)
                        {
                            list.Add(new UserSessionEvent
                            {
                                EventType = "logon",
                                Username = active.User,
                                Domain = active.Domain,
                                SessionId = active.SessionId,
                                LogonType = "Console",
                                OccurredAtUtc = now
                            });
                        }
                    }
                    else if (_lastActiveUser != active.User || _lastSessionState != "active")
                    {
                        list.Add(new UserSessionEvent
                        {
                            EventType = "unlock",
                            Username = active.User,
                            Domain = active.Domain,
                            SessionId = active.SessionId,
                            LogonType = "Console",
                            OccurredAtUtc = now
                        });
                        _lastActiveUser = active.User;
                        _lastSessionState = "active";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Session state inspection error");
        }

        return list.Count > 0 ? new UserSessionBatch { Events = list } : null;
    }
}
