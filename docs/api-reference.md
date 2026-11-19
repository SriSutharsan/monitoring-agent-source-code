# Endpoint Monitoring Agent — API Reference

> [!IMPORTANT]
> This document is generated from the source of truth: `Agent.Models`, `Agent.Communication`, and `Agent.Configuration`.
> Every field name, type, and enum value matches exactly what the agent sends and expects at runtime.

---

## Conventions

| Item | Value |
|---|---|
| Base URL | `https://<host>/api/v1/` (configured in `Agent:Api:BaseUrl` + `Agent:Api:ApiVersion`) |
| Content-Type | `application/json` (UTF-8, no BOM) |
| Serialisation | **camelCase** keys, nulls omitted, no indentation |
| Timestamps | ISO 8601 / RFC 3339, UTC, e.g. `"2026-11-19T04:37:02Z"` |
| Schema version | `"1.0"` (constant `SchemaInfo.SchemaVersion`) |
| TLS | TLS 1.2 minimum; TLS 1.3 used when the OS supports it |
| Redirects | **Not followed** — a redirect is treated as a rejection |

---

## Authentication

### Unauthenticated endpoints

`POST /api/v1/devices/register` and `GET /api/v1/ping` carry no credential.

### Authenticated endpoints

All other requests attach the DPAPI-protected device secret:

```
Authorization: Bearer <DeviceCredential.Secret>
X-Key-Id: <DeviceCredential.KeyId>          (omitted when KeyId is null/empty)
X-Agent-Version: <semver>
User-Agent: EndpointAgent/<semver>
Idempotency-Key: <string>                   (ingest calls only)
```

The credential is obtained from `RegistrationResponse.Credential` after the administrator approves the device.

---

## Endpoints

### `GET /api/v1/ping`

Health check. No body, no authentication.

**Success:** any 2xx — the agent only checks that the response is not a network error.

---

### `POST /api/v1/devices/register`

Registers the device or polls for approval status. Called repeatedly (with exponential back-off) until the response `status` is `"approved"` or `"rejected"`.

The full `inventory` block is sent only on the **first** request; subsequent polls omit it.

#### Request body — `RegistrationRequest`

```json
{
  "schemaVersion": "1.0",
  "deviceId":        "<stable UUID, regenerated on image clone>",
  "registrationToken": "<installer token>",
  "agentVersion":    "1.2.3",
  "hostname":        "DESKTOP-ABC123",
  "requestedUtc":    "2026-11-19T04:37:02Z",
  "metadata": {
    "group": "engineering"
  },
  "fingerprint": {
    "systemUuid":    "<DMI UUID>",
    "serialNumber":  "<chassis serial>",
    "manufacturer":  "Dell Inc.",
    "model":         "Latitude 5540",
    "macAddresses":  ["aa:bb:cc:dd:ee:ff"]
  },
  "inventory": {               // omitted after first successful POST
    "device":   { /* DeviceInfo */ },
    "hardware": { /* HardwareInventory */ },
    "storage":  { /* StorageInventory */ },
    "network":  { /* NetworkInfo */ }
  }
}
```

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | `string` | Always `"1.0"` |
| `deviceId` | `string` | Stable per device; reset when image clone detected |
| `registrationToken` | `string` | Shared secret set by the installer |
| `agentVersion` | `string` | Semver string |
| `hostname` | `string` | `Environment.MachineName` |
| `requestedUtc` | `datetime` | Agent clock at request time |
| `metadata` | `object<string,string>` | Installer-set tags; `"group"` is auto-added when configured |
| `fingerprint` | `DeviceFingerprint` | Used for duplicate-device detection |
| `inventory` | `RegistrationInventory` | First call only |

#### Response body — `RegistrationResponse`

```json
{
  "status":          "approved",
  "deviceId":        "<same as request>",
  "credential": {
    "secret":        "<bearer token>",
    "keyId":         "<rotation hint>",
    "expiresUtc":    "2027-11-19T04:37:02Z"
  },
  "pollAfterSeconds": 60,
  "message":         null,
  "serverTimeUtc":   "2026-11-19T04:37:02Z"
}
```

| `status` value | Meaning |
|---|---|
| `"pending"` | Waiting for administrator approval; keep polling |
| `"approved"` | Credential issued; begin syncing |
| `"rejected"` | Permanently denied; agent backs off 1 hour and logs the `message` |

**Agent back-off rules:**

| HTTP status | Action |
|---|---|
| `429` | Respect `Retry-After` header (or 2 min default) |
| `401 / 403` | Log error, back off 15 min — bad token |
| `4xx` | Back off 15 min |
| Network error | Exponential back-off, base 30 s, capped by `PollSeconds` |

---

### `POST /api/v1/devices/{deviceId}/{module}`

Ingest endpoint. Submits a batch of collected records for one module.

`{deviceId}` and `{module}` are URL-encoded.

**Module names** (from `Modules` constants):

| Module | Collector categories that feed it |
|---|---|
| `heartbeat` | `heartbeat` |
| `device` | `device` |
| `hardware` | `hardware`, `hardware.peripherals` |
| `storage` | `storage` |
| `network` | `network.config`, `network.wifi` |
| `performance` | `perf.cpu_memory`, `perf.disk`, `perf.network`, `battery` |
| `software` | `software` |
| `security` | `security` |
| `events` | `events` |
| `health` | `health` |
| `diagnostics` | `diagnostics` |
| `alerts` | `alerts` (pipeline-generated) |
| `changes` | `changes` (pipeline-generated) |

#### Request body — batch envelope

```json
{
  "schemaVersion": "1.0",
  "deviceId":      "<deviceId>",
  "module":        "performance",
  "sentUtc":       "2026-11-19T04:37:02Z",
  "records": [
    {
      "id":             "<idempotency key, SHA-256-based>",
      "category":       "perf.cpu_memory",
      "collectedAtUtc": "2026-11-19T04:36:57Z",
      "payload":        { /* module-specific object — see Payload Types below */ }
    }
  ]
}
```

> [!NOTE]
> `payload` is embedded verbatim from the SQLite outbox; it is never re-serialised. The batch size is capped by `BatchSize` (default 100, max 500).

**Request headers:**

```
Idempotency-Key: <SHA-256 hash of all record idempotency keys in this batch, truncated to 32 chars>
```

#### Response body — `IngestResponse`

```json
{
  "status":          "ok",
  "accepted":        ["<id1>", "<id2>"],
  "rejected": [
    {
      "id":        "<idempotency key>",
      "code":      "SCHEMA_ERROR",
      "message":   "unknown field 'foo'",
      "retryable": false
    }
  ],
  "serverTimeUtc":   "2026-11-19T04:37:02Z",
  "configVersion":   42,
  "nextSyncSeconds": 15,
  "update": {
    "manifest": {
      "payload":   "<base64(UTF-8 JSON of UpdateManifest)>",
      "signature": "<base64(RSA-SHA256 PKCS#1 v1.5 over payload bytes)>",
      "keyId":     "<signing key hint>"
    }
  }
}
```

| Field | Type | Notes |
|---|---|---|
| `accepted` | `string[]` | Idempotency keys the server accepted |
| `rejected` | `RejectedRecord[]` | Records the server did not accept |
| `configVersion` | `int64` | When higher than the agent's current version, the agent immediately pulls `/config` |
| `nextSyncSeconds` | `int` | Advisory next-sync interval; agent may ignore |
| `update.manifest` | `SignedEnvelope` | Optional update offer; `null` when no update is available |

**Agent acknowledgement logic:**

- A record not listed in `rejected` is considered **accepted**.
- `retryable: true` → scheduled for retry in 60 s.
- `retryable: false` → moved to dead-letter queue; never retried.
- If the whole batch gets a 4xx, records are split and re-sent one at a time.

**Agent retry rules:**

| HTTP status | Action |
|---|---|
| `2xx` | Mark records as synced |
| `401` | Mark retry +30 s; count toward re-registration (3 strikes) |
| `403` | Mark retry +10 min; device treated as revoked |
| `429` | Respect `Retry-After`; block drain loop |
| `4xx` (other) | Move batch to split-and-retry path |
| `5xx` / `408` / `425` | Exponential back-off on attempt count; re-queue |
| Network error | Exponential back-off; attempt counter **not** incremented |

---

### `GET /api/v1/devices/{deviceId}/config`

Returns the current remote configuration document as a signed envelope.

**Response body — `SignedEnvelope`:**

```json
{
  "payload":   "<base64(UTF-8 JSON of AgentConfigDocument)>",
  "signature": "<base64(RSA-SHA256 PKCS#1 v1.5 over payload bytes)>",
  "keyId":     "<signing key hint>"
}
```

The agent verifies the signature against the PEM public key in `Agent:Security:ConfigSigningPublicKey` before applying the document. Unsigned documents are rejected when `Agent:Security:RequireSignedConfig` is `true` (always true in production).

Additional validation:
- `expiresAtUtc` must be in the future.
- `deviceId` (when present in the document) must match this device.
- `version` must be strictly greater than the current cached version.
- `issuedAtUtc` must not be more than `MaxClockSkewMinutes` (default 10) in the future.

---

## Error body

All error responses share this shape:

```json
{
  "error": {
    "code":      "INVALID_FIELD",
    "message":   "Human-readable description",
    "requestId": "<correlation id>",
    "details": [
      { "field": "records[2].payload.cpuPercent", "issue": "must be 0–100" }
    ]
  }
}
```

---

## Payload Types

### `heartbeat`

```json
{
  "deviceId":              "<id>",
  "hostname":              "DESKTOP-ABC123",
  "agentVersion":          "1.2.3",
  "status":                "online",
  "agentUptimeSeconds":    3600,
  "systemUptimeSeconds":   86400,
  "configVersion":         42,
  "lastSuccessfulSyncUtc": "2026-11-19T04:00:00Z",
  "clockSkewMs":           -150,
  "pendingRecords":        12,
  "deadLetterRecords":     0,
  "primaryIPv4":           "192.168.1.50",
  "osVersion":             "10.0.19045",
  "elevated":              true,
  "agentCpuPercent":       0.4,
  "agentWorkingSetBytes":  52428800,
  "collectors": [
    {
      "category":           "perf.cpu_memory",
      "lastSuccessUtc":     "2026-11-19T04:36:57Z",
      "lastAttemptUtc":     "2026-11-19T04:36:57Z",
      "consecutiveFailures": 0,
      "lastError":          null,
      "lastDurationMs":     38,
      "enabled":            true
    }
  ]
}
```

---

### `device`

```json
{
  "deviceId":           "<id>",
  "hostname":           "DESKTOP-ABC123",
  "manufacturer":       "Dell Inc.",
  "model":              "Latitude 5540",
  "serialNumber":       "ABC1234",
  "systemUuid":         "<DMI UUID>",
  "chassisType":        "Notebook",
  "isLaptop":           true,
  "isVirtualMachine":   false,
  "assignedUser":       "jsmith",
  "activeSessions": [
    { "sessionId": 1, "user": "jsmith", "domain": "CORP", "state": "active", "station": "Console" }
  ],
  "domain":             "CORP",
  "partOfDomain":       true,
  "domainType":         "domain",
  "lastBootUtc":        "2026-11-18T22:00:00Z",
  "uptimeSeconds":      21600,
  "status":             "online",
  "timeZone":           "India Standard Time",
  "osCaption":          "Microsoft Windows 11 Pro",
  "osVersion":          "10.0.22631",
  "agentVersion":       "1.2.3",
  "agentRuntime":       ".NET Framework 4.8"
}
```

`domainType` values: `"domain"` | `"workgroup"`
`agentRuntime` values: `".NET Framework 4.8"` | `".NET 8.0"`

---

### `hardware`

`scope` is `"full"` (24 h inventory) or `"peripherals"` (5 min PnP scan).

```json
{
  "scope": "full",
  "processor": {
    "manufacturer": "Intel", "name": "Core i7-1365U",
    "architecture": "x64", "sockets": 1, "cores": 10, "logicalProcessors": 12,
    "maxClockMhz": 5200, "currentClockMhz": 2400,
    "l2CacheKb": 12288, "l3CacheKb": 12288,
    "currentUtilizationPercent": 12.5
  },
  "memory": {
    "totalBytes": 17179869184, "availableBytes": 10737418240,
    "usedBytes": 6442450944, "usedPercent": 37.5,
    "slotsTotal": 2, "slotsUsed": 2, "maxCapacityBytes": 34359738368,
    "modules": [
      {
        "slot": "DIMM0", "bankLabel": "Bank 0",
        "manufacturer": "Samsung", "partNumber": "M471A2G44AM0-CTD",
        "serialNumber": "00000001", "capacityBytes": 8589934592,
        "type": "LPDDR5", "formFactor": "Row Of Chips",
        "speedMhz": 4800, "configuredSpeedMhz": 4800
      }
    ]
  },
  "motherboard": {
    "manufacturer": "Dell Inc.", "product": "0XXXX", "version": "A03", "serialNumber": "ABC"
  },
  "bios": {
    "manufacturer": "Dell Inc.", "version": "1.23.0", "smbiosVersion": "3.3",
    "releaseDateUtc": "2024-06-01T00:00:00Z"
  },
  "gpus": [
    {
      "name": "Intel Iris Xe Graphics", "manufacturer": "Intel",
      "memoryBytes": 1073741824, "memorymayBeCapped": true,
      "driverVersion": "31.0.101.5333", "driverDateUtc": "2024-10-01T00:00:00Z",
      "currentHorizontalResolution": 1920, "currentVerticalResolution": 1200,
      "refreshRateHz": 60, "status": "OK"
    }
  ],
  "monitors": [
    {
      "manufacturer": "LEN", "model": "ThinkVision T27", "serialNumber": "V1234",
      "yearOfManufacture": 2023, "diagonalInches": 27.0,
      "instanceId": "DISPLAY\\LEN4036\\..."
    }
  ],
  "batteries": [
    {
      "name": "Primary", "manufacturer": "SMP", "serialNumber": "123",
      "chemistry": "Lithium Ion",
      "designCapacityMwh": 86000, "fullChargeCapacityMwh": 78000,
      "healthPercent": 90.7, "cycleCount": 42,
      "chargePercent": 85, "chargingStatus": "Discharging"
    }
  ],
  "peripherals": [
    {
      "name": "Logitech MX Master 3", "deviceClass": "Mouse",
      "manufacturer": "Logitech", "deviceId": "USB\\VID_046D&PID_C548\\...",
      "connection": "usb", "status": "OK"
    }
  ]
}
```

---

### `storage`

```json
{
  "disks": [
    {
      "deviceId": "\\\\.\\PhysicalDrive0", "index": 0,
      "manufacturer": "Samsung", "model": "SAMSUNG MZNLN256HAJQ",
      "serialNumber": "S3EXNX0K123456",
      "mediaType": "NVMe", "busType": "NVMe",
      "sizeBytes": 256060514304,
      "healthStatus": "Healthy", "operationalStatus": "OK",
      "smartPredictFailure": false,
      "temperatureC": 38, "powerOnHours": 4320, "wearPercent": 2,
      "smartSource": "storage-reliability",
      "firmwareVersion": "MAV21H3Q",
      "partitions": [
        {
          "index": 0, "type": "GPT: EFI System",
          "sizeBytes": 104857600, "startOffsetBytes": 1048576,
          "isBootPartition": true, "isPrimary": true,
          "driveLetters": []
        },
        {
          "index": 1, "type": "GPT: Basic Data",
          "sizeBytes": 255853010432, "startOffsetBytes": 105906176,
          "isBootPartition": false, "isPrimary": true,
          "driveLetters": ["C"]
        }
      ]
    }
  ],
  "volumes": [
    {
      "driveLetter": "C", "label": "Windows", "fileSystem": "NTFS",
      "driveType": "Fixed",
      "totalBytes": 255853010432, "freeBytes": 128000000000,
      "usedBytes": 127853010432, "usedPercent": 49.97,
      "isSystemDrive": true, "physicalDiskId": "\\\\.\\PhysicalDrive0"
    }
  ]
}
```

`mediaType` values: `"HDD"` | `"SSD"` | `"NVMe"` | `"Unknown"`
`healthStatus` values: `"Healthy"` | `"Warning"` | `"Unhealthy"` | `"Unknown"`
`smartSource` values: `"storage-reliability"` | `"ata-smart"` | `"none"`

---

### `network`

`scope` is `"configuration"` (adapters + IP) or `"wifi"` (Wi-Fi scan only).

```json
{
  "scope": "configuration",
  "hostname": "DESKTOP-ABC123",
  "primaryIPv4": "192.168.1.50",
  "adapters": [
    {
      "id": "{GUID}", "name": "Ethernet", "description": "Intel(R) Ethernet Connection",
      "manufacturer": "Intel", "model": "...",
      "macAddress": "AA:BB:CC:DD:EE:FF",
      "adapterType": "Ethernet", "status": "Up",
      "connectionStatus": "Connected",
      "isPhysical": true,
      "driverVersion": "12.19.2.37", "driverDateUtc": "2024-01-01T00:00:00Z",
      "driverProvider": "Intel",
      "speedBitsPerSecond": 1000000000,
      "ipv4Addresses": ["192.168.1.50"],
      "ipv6Addresses": ["fe80::1"],
      "subnetMasks": ["255.255.255.0"],
      "defaultGateways": ["192.168.1.1"],
      "dnsServers": ["8.8.8.8", "8.8.4.4"],
      "dhcpEnabled": true, "dhcpServer": "192.168.1.1",
      "dnsSuffix": "corp.example.com"
    }
  ],
  "wifi": {
    "adapterPresent": true, "connected": true,
    "adapterName": "Wi-Fi", "adapterModel": "Intel AX211",
    "ssid": "CorpWifi", "bssid": "aa:bb:cc:dd:ee:ff",
    "signalQualityPercent": 82, "signalStrengthDbm": -58,
    "frequencyMhz": 5180, "channel": 36,
    "band": "5 GHz", "radioType": "802.11ax", "wifiStandard": "Wi-Fi 6",
    "receiveRateMbps": 1200, "transmitRateMbps": 1200,
    "authentication": "WPA2-Enterprise", "cipher": "CCMP",
    "connectedSinceUtc": "2026-11-19T00:00:00Z",
    "connectionDurationSeconds": 17220,
    "state": "connected"
  }
}
```

`adapterType` values: `"Ethernet"` | `"Wi-Fi"` | `"VPN"` | `"Virtual"` | `"Loopback"` | `"Bluetooth"` | `"Mobile"` | `"Other"`

---

### `performance`

Each performance collector fills only its own sections; `sections` lists which ones are present.

```json
{
  "sections": ["cpu", "memory"],
  "cpu": {
    "usagePercent": 24.5,
    "logicalProcessors": 12,
    "currentClockMhz": 2400
  },
  "memory": {
    "totalBytes": 17179869184, "availableBytes": 9663676416,
    "usedBytes": 7516192768, "usedPercent": 43.75,
    "commitTotalBytes": 20000000000, "commitUsedBytes": 11000000000
  },
  "disks": [
    {
      "name": "PhysicalDrive0",
      "readBytesPerSec": 1048576, "writeBytesPerSec": 524288,
      "readIops": 128, "writeIops": 64,
      "busyPercent": 12.3, "queueLength": 0.4, "avgLatencyMs": 0.9
    }
  ],
  "network": {
    "downloadBytesPerSec": 2097152, "uploadBytesPerSec": 524288,
    "totalDownloadedBytes": 1073741824, "totalUploadedBytes": 536870912,
    "utilizationPercent": 2.1,
    "latencyMs": 18.3, "packetLossPercent": 0.0,
    "latencyTarget": "8.8.8.8",
    "internetReachable": true, "apiReachable": true,
    "vpnConnected": false, "vpnAdapters": [],
    "activeAdapter": "Ethernet",
    "events": [
      {
        "timestampUtc": "2026-11-19T03:00:00Z",
        "type": "adapter_down",
        "adapter": "Wi-Fi",
        "detail": "disconnected"
      }
    ]
  },
  "battery": {
    "present": true, "chargePercent": 85,
    "chargingStatus": "Discharging", "onAcPower": false,
    "estimatedMinutesRemaining": 210,
    "healthPercent": 90.7, "cycleCount": 42
  },
  "uptimeSeconds": 86400,
  "processCount": 234,
  "topCpuProcesses": [
    { "pid": 1234, "name": "chrome.exe", "cpuPercent": 8.3, "workingSetBytes": 314572800, "privateBytes": 209715200 }
  ],
  "topMemoryProcesses": [
    { "pid": 5678, "name": "outlook.exe", "cpuPercent": 0.5, "workingSetBytes": 524288000, "privateBytes": 419430400 }
  ],
  "temperatureC": 52.0,
  "temperatureSource": "MSAcpi_ThermalZoneTemperature"
}
```

`NetworkEvent.type` values: `"adapter_up"` | `"adapter_down"` | `"internet_lost"` | `"internet_restored"` | `"adapter_added"` | `"adapter_removed"`
`BatteryStatusInfo.chargingStatus` values: `"Charging"` | `"Discharging"` | `"Full"` | `"Unknown"`

---

### `software`

```json
{
  "os": {
    "caption": "Microsoft Windows 11 Pro",
    "edition": "Professional",
    "version": "10.0.22631",
    "displayVersion": "23H2",
    "buildNumber": "22631",
    "revision": 4169,
    "architecture": "x64",
    "servicePack": "",
    "installDateUtc": "2024-01-15T00:00:00Z",
    "activationStatus": "Licensed",
    "activationDetail": "Volume:MAK"
  },
  "applications": [
    {
      "key": "machine|x64|{GUID}",
      "name": "Microsoft Office 365",
      "version": "16.0.17928.20156",
      "publisher": "Microsoft Corporation",
      "installDateUtc": "2024-03-01T00:00:00Z",
      "installLocation": "C:\\Program Files\\Microsoft Office",
      "architecture": "x64",
      "scope": "machine",
      "uninstallCommand": "MsiExec.exe /X{GUID}",
      "quietUninstallCommand": "MsiExec.exe /X{GUID} /qn",
      "isUnauthorized": false,
      "policyMatch": null,
      "isOutdated": false,
      "minimumRequiredVersion": null,
      "isPotentiallyUnwanted": false
    }
  ],
  "updates": [
    {
      "id": "KB5031455",
      "description": "2023-10 Cumulative Update for Windows 11",
      "installedOnUtc": "2023-10-11T00:00:00Z",
      "installedBy": "NT AUTHORITY\\SYSTEM",
      "isSecurityUpdate": true
    }
  ],
  "updateSummary": {
    "latestSecurityUpdateId": "KB5031455",
    "latestSecurityUpdateUtc": "2023-10-11T00:00:00Z",
    "lastUpdateInstalledUtc": "2023-10-11T00:00:00Z",
    "pendingUpdateCount": 2,
    "pendingUpdateTitles": ["2024-01 Update for .NET", "Driver Update for Intel AX211"],
    "pendingDetection": "wua-cached",
    "recentFailures": [
      { "title": "KB5000123", "dateUtc": "2023-09-10T00:00:00Z", "result": "Failed", "hResult": "0x80070643" }
    ]
  }
}
```

`activationStatus` values: `"Licensed"` | `"Unlicensed"` | `"Grace"` | `"Notification"` | `"Unknown"`
`pendingDetection` values: `"wua-cached"` | `"unavailable"`
`InstalledApplication.scope` values: `"machine"` | `"user"`
`InstalledApplication.architecture` values: `"x86"` | `"x64"`

---

### `security`

```json
{
  "antivirus": {
    "products": [
      {
        "name": "Windows Defender",
        "enabled": true,
        "signaturesUpToDate": true,
        "rawState": 397568,
        "source": "security-center"
      }
    ],
    "defender": {
      "present": true,
      "serviceRunning": true,
      "antivirusEnabled": true,
      "antispywareEnabled": true,
      "realTimeProtectionEnabled": true,
      "tamperProtected": true,
      "engineVersion": "1.1.24110.3",
      "signatureVersion": "1.421.337.0",
      "signatureLastUpdatedUtc": "2026-11-19T02:00:00Z",
      "signatureAgeDays": 0,
      "lastQuickScanUtc": "2026-11-18T10:00:00Z",
      "lastFullScanUtc":  "2026-11-10T10:00:00Z",
      "recentThreats": [],
      "source": "wmi"
    }
  },
  "firewall": {
    "serviceRunning": true,
    "domainProfileEnabled": true,
    "privateProfileEnabled": true,
    "publicProfileEnabled": true,
    "activeProfiles": "Domain",
    "source": "com"
  },
  "tpm": {
    "present": true, "enabled": true, "activated": true,
    "specVersion": "2.0", "manufacturerVersion": "7.85"
  },
  "secureBoot": "Enabled",
  "encryption": {
    "bitLockerState": "Available",
    "systemDriveProtected": true,
    "deviceEncryptionActive": true,
    "volumes": [
      { "driveLetter": "C", "protectionStatus": "On", "conversionStatus": "FullyEncrypted", "encryptionMethod": "XtsAes256" }
    ]
  },
  "windowsUpdate": {
    "serviceEnabled": true,
    "autoUpdateMode": "AutoInstall",
    "lastSuccessfulInstallUtc": "2026-11-15T03:00:00Z",
    "daysSinceLastSuccessfulInstall": 4,
    "managedByPolicy": true
  },
  "pendingRestart": {
    "pending": false,
    "reasons": []
  },
  "uac": {
    "enabled": true,
    "consentPromptBehaviorAdmin": 5,
    "secureDesktopPrompt": true,
    "level": "Default"
  },
  "accountPolicy": {
    "minimumPasswordLength": 8,
    "maximumPasswordAgeDays": 90,
    "minimumPasswordAgeDays": 1,
    "passwordHistoryLength": 24,
    "lockoutThreshold": 10,
    "lockoutDurationMinutes": 30,
    "lockoutObservationMinutes": 30,
    "scope": "local-machine policy"
  },
  "elevated": true,
  "limitations": []
}
```

`secureBoot` values: `"Enabled"` | `"Disabled"` | `"Unsupported"` | `"Unknown"`
`EncryptionStatus.bitLockerState` values: `"Available"` | `"NotAvailable"` | `"Unknown"`
`BitLockerVolume.protectionStatus` values: `"On"` | `"Off"` | `"Unknown"`
`UacStatus.level` values: `"AlwaysNotify"` | `"Default"` | `"NoDim"` | `"Disabled"` | `"Custom"`
`FirewallStatus.source` values: `"com"` | `"registry"`
`DefenderStatus.source` values: `"wmi"` | `"registry"`

---

### `events`

```json
{
  "fromUtc": "2026-11-19T04:00:00Z",
  "toUtc":   "2026-11-19T04:36:57Z",
  "droppedCount": 0,
  "events": [
    {
      "deviceId":     "<id>",
      "log":          "System",
      "recordId":     1234567,
      "eventId":      7036,
      "source":       "Service Control Manager",
      "level":        "information",
      "message":      "The Windows Update service entered the stopped state.",
      "timestampUtc": "2026-11-19T04:10:00Z",
      "category":     "service_failure",
      "severity":     "warning",
      "computer":     "DESKTOP-ABC123",
      "metadata":     { "serviceName": "wuauserv", "state": "stopped" }
    }
  ]
}
```

`level` values: `"critical"` | `"error"` | `"warning"` | `"information"` | `"verbose"`

`category` values (from `EventCatalog`):
`"application_error"` | `"bsod"` | `"unexpected_shutdown"` | `"service_failure"` | `"driver_error"` | and others defined in `EventCatalog.cs`

---

### `health`

```json
{
  "score": 87,
  "status": "warning",
  "factors": [
    { "name": "disk_space", "status": "warning", "detail": "C: 50% full", "penalty": 10 },
    { "name": "defender_signatures", "status": "ok", "detail": "", "penalty": 0 }
  ],
  "recurringProblems": [
    {
      "key": "7036-Service Control Manager",
      "source": "System",
      "eventId": 7036,
      "description": "Service stopped unexpectedly",
      "occurrences": 7,
      "firstSeenUtc": "2026-11-15T08:00:00Z",
      "lastSeenUtc":  "2026-11-19T04:00:00Z"
    }
  ],
  "unexpectedShutdownsLast30Days": 1,
  "bugChecksLast30Days": 0,
  "lastBootUtc": "2026-11-18T22:00:00Z",
  "pendingRestart": false,
  "recentMaintenance": [
    {
      "timestampUtc": "2026-11-18T10:00:00Z",
      "type": "update_installed",
      "summary": "KB5031455 installed"
    }
  ]
}
```

`status` values: `"healthy"` | `"warning"` | `"critical"`
`HealthFactor.status` values: `"ok"` | `"warning"` | `"critical"` | `"unknown"`
`MaintenanceEntry.type` values: `"software_installed"` | `"update_installed"` | `"reboot"` | `"hardware_change"`

---

### `diagnostics`

Sent in response to a `DiagnosticRequest` embedded in the remote config document.

```json
{
  "requestId":   "<from AgentConfigDocument.Diagnostics.RequestId>",
  "generatedUtc": "2026-11-19T04:37:00Z",
  "sections":    ["services", "problem_devices", "startup_items", "recent_errors"],
  "stoppedAutoStartServices": [
    { "name": "wuauserv", "displayName": "Windows Update", "state": "Stopped", "startMode": "Auto", "exitCode": 0 }
  ],
  "problemDevices": [
    { "name": "Unknown Device", "deviceClass": "USB", "deviceId": "USB\\VID_0000...", "errorCode": 43, "errorDescription": "Windows has stopped this device" }
  ],
  "startupItems": [
    { "name": "OneDrive", "location": "HKCU\\...\\Run", "command": "\"C:\\...\\OneDrive.exe\" /background" }
  ],
  "recentErrors": [ /* WindowsEventRecord[] — same shape as events module */ ],
  "errors": []
}
```

Sections requested via `AgentConfigDocument.Diagnostics.Sections`: `"services"` | `"problem_devices"` | `"startup_items"` | `"recent_errors"`

---

### `alerts`

Pipeline-generated. One record per alert state transition.

```json
{
  "alertKey":     "cpu_sustained|system",
  "rule":         "cpu_sustained",
  "subject":      "system",
  "state":        "raised",
  "severity":     "warning",
  "message":      "CPU usage sustained above 90% for 3 samples",
  "value":        91.3,
  "threshold":    90.0,
  "timestampUtc": "2026-11-19T04:36:57Z",
  "metadata":     { "sustainedSamples": "3" }
}
```

`state` values: `"raised"` | `"cleared"` | `"event"`
`severity` values: `"info"` | `"warning"` | `"critical"`

---

### `changes`

Pipeline-generated. One record per detected inventory change.

```json
{
  "category":    "hardware",
  "changeClass": "ram_changed",
  "changeType":  "modified",
  "itemKind":    "MemoryModule",
  "itemKey":     "DIMM0",
  "displayName": "DIMM0 — Samsung 8 GB LPDDR5",
  "summary":     "Memory module capacity changed",
  "before":      "8 GB",
  "after":       "16 GB",
  "detectedUtc": "2026-11-19T04:36:57Z"
}
```

`changeType` values: `"added"` | `"removed"` | `"modified"`
`category` values: `"hardware"` | `"storage"` | `"network"` | `"software"` (and others)

---

## Remote Configuration (`AgentConfigDocument`)

Delivered as the `payload` (base64 JSON) inside a `SignedEnvelope` from `GET /config`.

```json
{
  "version":      42,
  "issuedAtUtc":  "2026-11-19T00:00:00Z",
  "expiresAtUtc": "2026-11-26T00:00:00Z",
  "deviceId":     null,
  "intervalsSeconds": {
    "heartbeat":        60,
    "device":          300,
    "hardware":        86400,
    "hardware.peripherals": 300,
    "storage":         3600,
    "network.config":  3600,
    "network.wifi":    300,
    "perf.cpu_memory":   30,
    "perf.disk":         60,
    "perf.network":      60,
    "battery":          120,
    "software":        86400,
    "security":        3600,
    "events":           120,
    "health":          3600,
    "diagnostics":     3600
  },
  "collectorsEnabled": {
    "battery": false
  },
  "syncIntervalSeconds": 15,
  "batchSize": 100,
  "thresholds": {
    "storageWarningPercent":    80,
    "storageCriticalPercent":   90,
    "criticalFreeBytes":        2147483648,
    "criticalFreePercent":      5,
    "cpuPercent":               90,
    "ramPercent":               90,
    "sustainedSamples":         3,
    "diskBusyPercent":          95,
    "diskQueueLength":          8,
    "processCpuPercent":        80,
    "processSustainedSamples":  10,
    "latencyMs":                300,
    "packetLossPercent":        10,
    "networkFlapCount":         3,
    "networkFlapWindowMinutes": 15,
    "batteryLowPercent":        15,
    "batteryHealthLowPercent":  50,
    "signatureMaxAgeDays":      7,
    "failedLogonCount":         5,
    "failedLogonWindowMinutes": 10,
    "diskTemperatureC":         65,
    "systemTemperatureC":       90,
    "pendingRestartDays":       7,
    "recurringProblemCount":    5,
    "recurringProblemWindowHours": 24
  },
  "retention": {
    "syncedRetentionDays":  7,
    "pendingMaxAgeDays":    30,
    "maxPendingRecords":    200000,
    "healthHistoryDays":    30,
    "changeHistoryDays":    365
  },
  "softwarePolicy": {
    "blockedApplications":  ["TeamViewer*", "uTorrent*"],
    "allowedApplications":  [],
    "minimumVersions": [
      { "namePattern": "Google Chrome*", "minimumVersion": "120.0" }
    ],
    "potentiallyUnwantedPatterns": ["*crack*", "*keygen*"],
    "allowListIgnoresMicrosoftComponents": true
  },
  "events": {
    "firstRunLookbackMinutes": 360,
    "maxEventsPerCycle":       500,
    "maxMessageLength":        1000,
    "collectSuccessfulLogons": true
  },
  "networkProbes": {
    "pingTargets":   ["8.8.8.8", "1.1.1.1"],
    "pingTimeoutMs": 1500,
    "pingGateway":   true
  },
  "diagnostics": {
    "requestId":  "<uuid>",
    "expiresUtc": "2026-11-20T00:00:00Z",
    "sections":   ["services", "problem_devices"]
  }
}
```

> [!NOTE]
> `deviceId` in the document is optional. When set, the agent rejects the config if it does not match its own device ID.
> `diagnostics` is `null` when no diagnostic run is requested.

---

## Update Manifest

Embedded as `payload` (base64 JSON) inside the `SignedEnvelope` in `IngestResponse.Update.Manifest`.

```json
{
  "version":      "1.3.0",
  "packageUrl":   "https://updates.example.com/agent-1.3.0.zip",
  "sha256":       "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  "sizeBytes":    8388608,
  "runtime":      "net48",
  "issuedAtUtc":  "2026-11-19T00:00:00Z",
  "expiresAtUtc": "2026-11-26T00:00:00Z"
}
```

`runtime` values: `"net48"` | `"net8"` | `null` (any runtime accepts it)

The agent validates signature, `sha256` of the downloaded package, `expiresAtUtc`, and `issuedAtUtc` clock skew before applying the update.

---

## `appsettings.json` Reference

```json
{
  "Agent": {
    "ServiceName": "EndpointMonitoringAgent",
    "DataDirectory": "C:\\ProgramData\\EndpointMonitoringAgent",
    "Api": {
      "BaseUrl":                "https://monitor.example.com",
      "ApiVersion":             "v1",
      "RequestTimeoutSeconds":  30,
      "MaxRetryDelaySeconds":   900,
      "PinnedCertificateSha256": [],
      "ProxyUrl":               "",
      "AllowInsecureLoopback":  false
    },
    "Registration": {
      "Token":       "<installer token>",
      "Group":       "engineering",
      "Metadata":    { "site": "london" },
      "PollSeconds": 60
    },
    "Logging": {
      "MinimumLevel":        "Information",
      "RetainedFileCount":   14,
      "FileSizeLimitBytes":  10485760,
      "WriteToEventLog":     true
    },
    "Security": {
      "ConfigSigningPublicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----",
      "RequireSignedConfig":    true,
      "MaxClockSkewMinutes":    10
    },
    "Collection": {
      "IntervalsSeconds":       { "perf.cpu_memory": 30 },
      "CollectorsEnabled":      { "battery": false },
      "SyncIntervalSeconds":    15,
      "BatchSize":              100,
      "MaxConcurrentCollections": 2,
      "SyncedRetentionDays":    7,
      "CollectSuccessfulLogons": true,
      "PingTargets":            ["8.8.8.8"]
    }
  }
}
```

> [!CAUTION]
> `AllowInsecureLoopback: true` is for development mock servers **only**. Never set this in a production deployment.
> `RequireSignedConfig: false` disables signature verification. Never set this in production.
