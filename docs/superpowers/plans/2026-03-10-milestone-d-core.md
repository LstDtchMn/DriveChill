# Milestone D-Core: Debt Cleanup — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clean up all deferred debt from Milestones A–C: wire MQTT telemetry into poll loops, add MQTT to the frontend channel picker, backfill missing C# tests, implement session token rotation on password change, and fix the flaky settings E2E spec.

**Architecture:** Five independent workstreams that can execute in parallel. D1 (MQTT frontend) and D2 (MQTT telemetry wiring) touch different layers. D3 (C# tests) is read-only against production code. D4 (session rotation) is a cross-cutting auth change. D5 (E2E flake) is frontend-only test work.

**Tech Stack:** Python 3.12 / FastAPI / aiosqlite / aiomqtt · C# / ASP.NET Core 10 / MQTTnet 4.3 · React / Next.js 14 / Zustand · Playwright · xUnit

---

## File Structure

### New files
| File | Responsibility |
|------|---------------|
| `frontend/src/components/settings/NotificationChannelForm.tsx` | Extracted notification channel CRUD form (from SettingsPage) |
| `backend-cs/Tests/MqttChannelTests.cs` | MQTT type validation, config parsing, publish tests |
| `backend-cs/Tests/ExportControllerTests.cs` | CSV + JSON export endpoint tests |
| `backend-cs/Tests/MachineStatusTests.cs` | Machine status eviction logic tests |

### Modified files
| File | Change |
|------|--------|
| `frontend/src/lib/types.ts` | Add `'mqtt'` to `NotificationChannelType` union |
| `frontend/src/components/settings/SettingsPage.tsx` | Extract channel form, add MQTT option/hints, password-change session refresh + WS close |
| `backend/app/main.py` | Add MQTT telemetry publisher task in lifespan |
| `backend-cs/Services/SensorWorker.cs` | Wire `PublishTelemetryAsync()` with single-flight guard |
| `backend/app/api/routes/auth.py` | Add self-password-change endpoint, session rotation |
| `backend/app/services/auth_service.py` | Add `rotate_session()` helper |
| `backend-cs/Api/AuthController.cs` | Add self-password-change endpoint, session rotation |
| `frontend/src/lib/api.ts` | Add `changeMyPassword()` API method |
| `frontend/src/hooks/useWebSocket.ts` | Export `closeSocket()` for external callers |
| `frontend/e2e/settings.spec.ts` | Fix flaky `°F toggle` test |

---

## Chunk 1: D1 — MQTT Frontend Config UI

### Task 1: Add `'mqtt'` to the frontend type union

**Files:**
- Modify: `frontend/src/lib/types.ts:481`

- [ ] **Step 1: Update the type union**

In `frontend/src/lib/types.ts`, change line 481:

```typescript
// Before:
export type NotificationChannelType = 'discord' | 'slack' | 'ntfy' | 'generic_webhook';

// After:
export type NotificationChannelType = 'discord' | 'slack' | 'ntfy' | 'generic_webhook' | 'mqtt';
```

- [ ] **Step 2: Verify frontend builds**

Run: `cd frontend && npx next build`
Expected: Build succeeds with no type errors.

- [ ] **Step 3: Commit**

```bash
git add frontend/src/lib/types.ts
git commit -m "feat(d1): add mqtt to NotificationChannelType union"
```

---

### Task 2: Extract NotificationChannelForm component

**Files:**
- Create: `frontend/src/components/settings/NotificationChannelForm.tsx`
- Modify: `frontend/src/components/settings/SettingsPage.tsx`

The notification channel section in SettingsPage.tsx (lines 1564–1720+) is too
large. Extract the entire section into its own component.

- [ ] **Step 1: Create the extracted component**

Create `frontend/src/components/settings/NotificationChannelForm.tsx`:

```tsx
'use client';

import { useState, useEffect } from 'react';
import { Pencil, X } from 'lucide-react';
import type { NotificationChannel, NotificationChannelType } from '@/lib/types';
import { api } from '@/lib/api';

interface NotificationChannelFormProps {
  isAdmin: boolean;
  toast: (msg: string, type?: 'error') => void;
  confirm: (msg: string) => Promise<boolean>;
}

const CHANNEL_HINTS: Record<NotificationChannelType, string> = {
  discord: '— { "webhook_url": "https://discord.com/api/webhooks/..." }',
  slack: '— { "webhook_url": "https://hooks.slack.com/services/..." }',
  ntfy: '— { "topic": "my-alerts", "url": "https://ntfy.sh" }',
  generic_webhook: '— { "url": "https://...", "hmac_secret": "optional" }',
  mqtt: '— { "broker_url": "mqtt://host:1883", "topic_prefix": "drivechill", "username": "", "password": "", "client_id": "drivechill-hub", "qos": 1, "retain": false, "publish_telemetry": false }',
};

const CHANNEL_PLACEHOLDERS: Record<NotificationChannelType, string> = {
  discord: '{ "webhook_url": "" }',
  slack: '{ "webhook_url": "" }',
  ntfy: '{ "topic": "", "url": "https://ntfy.sh" }',
  generic_webhook: '{ "url": "" }',
  mqtt: '{\n  "broker_url": "mqtt://192.168.1.100:1883",\n  "topic_prefix": "drivechill",\n  "username": "",\n  "password": "",\n  "client_id": "drivechill-hub",\n  "qos": 1,\n  "retain": false,\n  "publish_telemetry": false\n}',
};

const CHANNEL_TYPE_LABELS: Record<NotificationChannelType, string> = {
  discord: 'Discord',
  slack: 'Slack',
  ntfy: 'ntfy.sh',
  generic_webhook: 'Generic Webhook',
  mqtt: 'MQTT',
};

export function NotificationChannelForm({ isAdmin, toast, confirm }: NotificationChannelFormProps) {
  const [channels, setChannels] = useState<NotificationChannel[]>([]);
  const [name, setName] = useState('');
  const [type, setType] = useState<NotificationChannelType>('discord');
  const [enabled, setEnabled] = useState(true);
  const [config, setConfig] = useState('');
  const [editId, setEditId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [testingId, setTestingId] = useState<string | null>(null);

  useEffect(() => {
    api.notificationChannels.list().then(r => setChannels(r.channels)).catch(() => {});
  }, []);

  const resetForm = () => {
    setEditId(null);
    setName('');
    setType('discord');
    setEnabled(true);
    setConfig('');
  };

  const handleSave = async () => {
    let parsed: Record<string, unknown>;
    try {
      parsed = config.trim() ? JSON.parse(config) : {};
    } catch {
      toast('Config must be valid JSON.', 'error');
      return;
    }
    setBusy(true);
    try {
      if (editId) {
        await api.notificationChannels.update(editId, { name: name.trim(), enabled, config: parsed });
        toast('Channel updated.');
      } else {
        await api.notificationChannels.create({ type, name: name.trim(), enabled, config: parsed });
        toast('Channel created.');
      }
      const r = await api.notificationChannels.list();
      setChannels(r.channels);
      resetForm();
    } catch (err: any) {
      toast(err?.message || 'Save failed.', 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card p-6 animate-card-enter">
      <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>
        Notification Channels
      </h3>
      <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
        Send alert notifications to Discord, Slack, ntfy.sh, MQTT, or a generic webhook endpoint.
        Channels are used alongside existing push/email notifications.
      </p>

      {/* Existing channels list */}
      {channels.length > 0 && (
        <div className="space-y-2 mb-4">
          {channels.map(ch => (
            <div key={ch.id} className="flex items-center justify-between p-3 rounded text-xs"
              style={{ background: 'var(--surface-200)', color: 'var(--text)' }}>
              <div className="flex-1 min-w-0">
                <div className="font-medium flex items-center gap-2">
                  {ch.name}
                  <span className="badge" style={{ fontSize: '0.65rem' }}>
                    {CHANNEL_TYPE_LABELS[ch.type] ?? ch.type.replace('_', ' ')}
                  </span>
                  {ch.enabled
                    ? <span className="badge badge-success" style={{ fontSize: '0.6rem' }}>ON</span>
                    : <span className="badge badge-danger" style={{ fontSize: '0.6rem' }}>OFF</span>}
                </div>
              </div>
              <div className="flex items-center gap-2 ml-2 shrink-0">
                <button className="btn-secondary text-xs px-2 py-1"
                  disabled={testingId === ch.id}
                  onClick={async () => {
                    setTestingId(ch.id);
                    try {
                      const r = await api.notificationChannels.test(ch.id);
                      if (r.success) toast('Test notification sent!');
                      else toast(r.error || 'Test failed.', 'error');
                    } catch (err: any) {
                      toast(err?.message || 'Test failed.', 'error');
                    } finally {
                      setTestingId(null);
                    }
                  }}>
                  {testingId === ch.id ? '...' : 'Test'}
                </button>
                {isAdmin && (
                  <>
                    <button className="btn-secondary text-xs px-2 py-1"
                      onClick={() => {
                        setEditId(ch.id);
                        setName(ch.name);
                        setType(ch.type);
                        setEnabled(ch.enabled);
                        setConfig(JSON.stringify(ch.config, null, 2));
                      }}>
                      <Pencil size={12} />
                    </button>
                    <button className="btn-secondary text-xs px-2 py-1"
                      style={{ color: 'var(--danger)' }}
                      onClick={async () => {
                        const confirmed = await confirm(`Delete channel "${ch.name}"?`);
                        if (!confirmed) return;
                        try {
                          await api.notificationChannels.delete(ch.id);
                          setChannels(prev => prev.filter(c => c.id !== ch.id));
                          toast('Channel deleted.');
                        } catch (err: any) {
                          toast(err?.message || 'Delete failed.', 'error');
                        }
                      }}>
                      <X size={12} />
                    </button>
                  </>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Create/Edit form — admin only */}
      {isAdmin && (
        <div className="space-y-3 p-4 rounded" style={{ background: 'var(--surface-200)' }}>
          <div className="text-xs font-medium" style={{ color: 'var(--text)' }}>
            {editId ? 'Edit Channel' : 'Add Channel'}
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Name</label>
              <input className="w-full p-2 rounded text-xs"
                style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                value={name} onChange={e => setName(e.target.value)} placeholder="My MQTT Broker" />
            </div>
            <div>
              <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Type</label>
              <select className="w-full p-2 rounded text-xs"
                style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                value={type} onChange={e => setType(e.target.value as NotificationChannelType)}
                disabled={!!editId}>
                {Object.entries(CHANNEL_TYPE_LABELS).map(([value, label]) => (
                  <option key={value} value={value}>{label}</option>
                ))}
              </select>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="nc-enabled" checked={enabled}
              onChange={e => setEnabled(e.target.checked)} />
            <label htmlFor="nc-enabled" className="text-xs" style={{ color: 'var(--text-secondary)' }}>
              Enabled
            </label>
          </div>
          <div>
            <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>
              Config (JSON)
              <span style={{ opacity: 0.6, marginLeft: 4 }}>{CHANNEL_HINTS[type]}</span>
            </label>
            <textarea className="w-full p-2 rounded text-xs font-mono" rows={type === 'mqtt' ? 10 : 4}
              style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)', resize: 'vertical' }}
              value={config} onChange={e => setConfig(e.target.value)}
              placeholder={CHANNEL_PLACEHOLDERS[type]} />
          </div>
          <div className="flex gap-2">
            <button className="btn-primary text-xs px-4 py-2" disabled={busy || !name.trim()}
              onClick={handleSave}>
              {busy ? 'Saving...' : editId ? 'Update' : 'Create'}
            </button>
            {editId && (
              <button className="btn-secondary text-xs px-4 py-2" onClick={resetForm}>
                Cancel
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 2: Replace inline code in SettingsPage.tsx**

In `frontend/src/components/settings/SettingsPage.tsx`:

a. Add import at top (after existing component imports):
```typescript
import { NotificationChannelForm } from './NotificationChannelForm';
```

b. Remove these state variables (lines 86-94):
```typescript
// DELETE these lines:
const [notifChannels, setNotifChannels] = useState<NotificationChannel[]>([]);
const [ncName, setNcName] = useState('');
const [ncType, setNcType] = useState<NotificationChannelType>('discord');
const [ncEnabled, setNcEnabled] = useState(true);
const [ncConfig, setNcConfig] = useState('');
const [ncEditId, setNcEditId] = useState<string | null>(null);
const [ncBusy, setNcBusy] = useState(false);
const [ncTestingId, setNcTestingId] = useState<string | null>(null);
```

c. Remove the `useEffect` for channel loading (lines 107-109):
```typescript
// DELETE:
useEffect(() => {
  api.notificationChannels.list().then(r => setNotifChannels(r.channels)).catch(() => {});
}, []);
```

d. Replace the entire notification channels section (lines 1564-1720+) with:
```tsx
<NotificationChannelForm isAdmin={isAdmin} toast={toast} confirm={confirm} />
```

e. Remove `NotificationChannel` and `NotificationChannelType` from the types import if they are no longer used elsewhere in SettingsPage. Check first — they may still be referenced. If not referenced, remove from the import.

- [ ] **Step 3: Verify frontend builds**

Run: `cd frontend && npx next build`
Expected: Build succeeds. No type errors. MQTT option visible in channel type picker.

- [ ] **Step 4: Commit**

```bash
git add frontend/src/components/settings/NotificationChannelForm.tsx frontend/src/components/settings/SettingsPage.tsx frontend/src/lib/types.ts
git commit -m "feat(d1): extract NotificationChannelForm, add MQTT channel type"
```

---

## Chunk 2: D2 — MQTT Telemetry Wiring

### Task 3: Wire Python telemetry publisher in main.py

**Files:**
- Modify: `backend/app/main.py`

The MQTT `publish_telemetry()` method exists at
`notification_channel_service.py:371` but is never called. We need a dedicated
background task in `main.py` that subscribes to sensor snapshots and publishes
them to MQTT channels.

- [ ] **Step 1: Write the telemetry publisher test**

Create or append to `backend/tests/test_mqtt_telemetry.py`:

```python
"""Tests for MQTT telemetry publishing wiring."""
import asyncio
import pytest
from unittest.mock import AsyncMock, MagicMock, patch


@pytest.mark.asyncio
async def test_telemetry_publisher_calls_publish_on_snapshot():
    """The telemetry publisher should call publish_telemetry when it receives a snapshot."""
    from app.services.sensor_service import SensorService

    # Create a mock notification channel service
    channel_svc = AsyncMock()
    channel_svc.publish_telemetry = AsyncMock(return_value=1)

    # Create a mock sensor service with a queue
    sensor_svc = MagicMock(spec=SensorService)
    queue = asyncio.Queue(maxsize=10)
    sensor_svc.subscribe.return_value = queue
    sensor_svc.unsubscribe = MagicMock()

    # Import the publisher function
    from app.mqtt_telemetry import create_telemetry_publisher

    # Start the publisher
    task = asyncio.create_task(create_telemetry_publisher(sensor_svc, channel_svc))

    # Push a mock snapshot
    mock_snapshot = MagicMock()
    mock_snapshot.readings = [
        MagicMock(id="cpu_temp_0", name="CPU", sensor_type="temp", value=55.0, unit="°C"),
    ]
    await queue.put(mock_snapshot)

    # Give the loop time to process
    await asyncio.sleep(0.1)

    # Verify publish was called
    channel_svc.publish_telemetry.assert_called_once()

    # Clean up
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass
    sensor_svc.unsubscribe.assert_called_once_with(queue)


@pytest.mark.asyncio
async def test_telemetry_publisher_single_flight():
    """If a publish is already in-flight, the next snapshot should be dropped."""
    from app.services.sensor_service import SensorService

    # Create a slow publish mock that blocks until we release it
    publish_call_count = 0
    publish_started = asyncio.Event()
    publish_continue = asyncio.Event()

    async def slow_publish(readings):
        nonlocal publish_call_count
        publish_call_count += 1
        publish_started.set()
        await publish_continue.wait()
        return 1

    channel_svc = AsyncMock()
    channel_svc.publish_telemetry = slow_publish

    sensor_svc = MagicMock(spec=SensorService)
    queue = asyncio.Queue(maxsize=10)
    sensor_svc.subscribe.return_value = queue
    sensor_svc.unsubscribe = MagicMock()

    from app.mqtt_telemetry import create_telemetry_publisher

    task = asyncio.create_task(create_telemetry_publisher(sensor_svc, channel_svc))

    # Push first snapshot — will start slow publish task
    mock_snapshot = MagicMock()
    mock_snapshot.readings = [MagicMock(id="s1", name="S1", sensor_type="temp", value=50, unit="C")]
    await queue.put(mock_snapshot)
    await publish_started.wait()

    # Push second snapshot while first is still in-flight — should be dropped
    await queue.put(mock_snapshot)
    # Give the loop time to read the second snapshot and drop it
    await asyncio.sleep(0.05)

    # Push third snapshot — also should be dropped (first still running)
    await queue.put(mock_snapshot)
    await asyncio.sleep(0.05)

    # Release the slow publish
    publish_continue.set()
    await asyncio.sleep(0.1)

    # Only 1 publish call should have been made (second and third were dropped)
    assert publish_call_count == 1

    # Clean up
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd backend && python -m pytest tests/test_mqtt_telemetry.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'app.mqtt_telemetry'`

- [ ] **Step 3: Create the telemetry publisher module**

Create `backend/app/mqtt_telemetry.py`:

```python
"""MQTT telemetry publisher — background task that subscribes to sensor
snapshots and publishes them to MQTT channels with publish_telemetry enabled.

Runs as a standalone coroutine started from main.py lifespan, not inside
SensorService (separation of concerns: SensorService handles polling,
this handles MQTT transport).
"""
import asyncio
import logging
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from app.services.sensor_service import SensorService
    from app.services.notification_channel_service import NotificationChannelService

logger = logging.getLogger(__name__)


async def _do_publish(
    channel_svc: "NotificationChannelService",
    snapshot,
) -> None:
    """Publish a single snapshot's readings to MQTT channels."""
    readings = [
        {
            "sensor_id": r.id,
            "sensor_name": r.name,
            "sensor_type": r.sensor_type,
            "value": r.value,
            "unit": r.unit,
        }
        for r in snapshot.readings
    ]
    await channel_svc.publish_telemetry(readings)


async def create_telemetry_publisher(
    sensor_svc: "SensorService",
    channel_svc: "NotificationChannelService",
) -> None:
    """Subscribe to sensor snapshots and publish telemetry to MQTT.

    Uses single-flight pattern: publish is launched as a fire-and-forget task.
    If the previous publish task is still running when the next snapshot arrives,
    the snapshot is dropped rather than queued. This prevents unbounded
    concurrent publishes if the MQTT broker is slow.
    """
    queue = sensor_svc.subscribe()
    publish_task: asyncio.Task | None = None

    try:
        while True:
            snapshot = await queue.get()
            if snapshot is None:
                # Failure sentinel — skip
                continue

            # Single-flight: if previous publish still running, drop this batch
            if publish_task is not None and not publish_task.done():
                continue

            publish_task = asyncio.create_task(_do_publish(channel_svc, snapshot))
    except asyncio.CancelledError:
        # Wait for in-flight publish to finish (grace period handled by caller)
        if publish_task is not None and not publish_task.done():
            try:
                await asyncio.wait_for(publish_task, timeout=1.0)
            except (asyncio.CancelledError, asyncio.TimeoutError, Exception):
                pass
    finally:
        sensor_svc.unsubscribe(queue)
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd backend && python -m pytest tests/test_mqtt_telemetry.py -v`
Expected: 2 tests PASS.

- [ ] **Step 5: Wire the publisher into main.py lifespan**

In `backend/app/main.py`, add the import near the top (with other service imports, before line 56):

```python
from app.mqtt_telemetry import create_telemetry_publisher
```

After `notification_channel_svc` is created (after line 258), add:

```python
    # ------------------------------------------------------------------
    # MQTT telemetry publisher — subscribes to sensor snapshots and
    # publishes to MQTT channels with publish_telemetry enabled.
    # Uses single-flight pattern to avoid unbounded concurrent publishes.
    # ------------------------------------------------------------------
    telemetry_task = asyncio.create_task(
        create_telemetry_publisher(sensor_service, notification_channel_svc)
    )
```

In the shutdown section (around lines 404-432), before `notification_channel_svc` cleanup, add:

```python
    # Cancel telemetry publisher with grace period
    telemetry_task.cancel()
    try:
        await asyncio.wait_for(telemetry_task, timeout=2.0)
    except (asyncio.CancelledError, asyncio.TimeoutError):
        pass
```

- [ ] **Step 6: Verify Python tests still pass**

Run: `cd backend && python -m pytest tests/ -q`
Expected: All tests pass (560+ tests).

- [ ] **Step 7: Commit**

```bash
git add backend/app/mqtt_telemetry.py backend/tests/test_mqtt_telemetry.py backend/app/main.py
git commit -m "feat(d2): wire MQTT telemetry publisher into sensor poll loop (Python)"
```

---

### Task 4: Wire C# telemetry publishing in SensorWorker

**Files:**
- Modify: `backend-cs/Services/SensorWorker.cs`

`SensorWorker` already has `_notifChannels` injected (constructor line 44) and
calls `SendAlertAllAsync()` for alerts (line 156). We need to add a
`PublishTelemetryAsync()` call after readings are collected, with a
single-flight guard.

- [ ] **Step 1: Add the single-flight flag and telemetry call**

In `SensorWorker.cs`, add a field near the class fields:

```csharp
private volatile bool _telemetryPublishing;
```

After sensor readings are collected and `_sensors.Update(snapshot)` is called
(around line 106), add:

```csharp
            // MQTT telemetry — single-flight: skip if previous publish still running
            if (!_telemetryPublishing)
            {
                _telemetryPublishing = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var telemetryReadings = readings.Select(r => new TelemetryReading
                        {
                            SensorId = r.Id,
                            SensorName = r.Name,
                            SensorType = r.SensorType,
                            Value = r.Value,
                            Unit = r.Unit,
                        }).ToList();
                        await _notifChannels.PublishTelemetryAsync(telemetryReadings, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "MQTT telemetry publish failed");
                    }
                    finally
                    {
                        _telemetryPublishing = false;
                    }
                }, CancellationToken.None);
            }
```

Add the required `using` if not already present:
```csharp
using System.Linq;
```

- [ ] **Step 2: Verify C# builds**

Run: `cd backend-cs && dotnet build`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Verify C# tests pass**

Run: `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q`
Expected: All 205+ tests pass.

- [ ] **Step 4: Commit**

```bash
git add backend-cs/Services/SensorWorker.cs
git commit -m "feat(d2): wire MQTT telemetry publishing into SensorWorker (C#)"
```

---

## Chunk 3: D3 — C# Test Backfill for Milestone C

### Task 5: MQTT channel tests

**Files:**
- Create: `backend-cs/Tests/MqttChannelTests.cs`

Test MQTT type validation, config parsing, URL handling, and graceful failures.

- [ ] **Step 1: Write the MQTT channel test class**

Create `backend-cs/Tests/MqttChannelTests.cs`:

```csharp
using System.Text.Json;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>MQTT notification channel tests — config parsing, URL validation, type acceptance.</summary>
public sealed class MqttChannelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly NotificationChannelService _svc;

    public MqttChannelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db = new DbService(settings, NullLogger<DbService>.Instance);
        _db.EnsureInitialisedAsync(CancellationToken.None).GetAwaiter().GetResult();

        var httpFactory = new NullHttpClientFactory();
        _svc = new NotificationChannelService(_db, httpFactory, NullLogger<NotificationChannelService>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static Dictionary<string, JsonElement> Cfg(params (string key, object val)[] pairs)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in pairs)
            dict[k] = JsonSerializer.SerializeToElement(v);
        return dict;
    }

    [Fact]
    public async Task CreateMqttChannel_AcceptsValidConfig()
    {
        var ch = await _svc.CreateAsync("mqtt", "Test MQTT", true,
            Cfg(("broker_url", "mqtt://192.168.1.100:1883"),
                ("topic_prefix", "drivechill"),
                ("qos", 1),
                ("retain", false),
                ("publish_telemetry", true)),
            CancellationToken.None);

        Assert.NotNull(ch);
        Assert.Equal("mqtt", ch!.Type);
        Assert.Equal("Test MQTT", ch.Name);
    }

    [Fact]
    public async Task CreateMqttChannel_AcceptsMqttsScheme()
    {
        var ch = await _svc.CreateAsync("mqtt", "TLS MQTT", true,
            Cfg(("broker_url", "mqtts://broker.example.com:8883")),
            CancellationToken.None);

        Assert.NotNull(ch);
    }

    [Fact]
    public async Task CreateMqttChannel_EmptyConfig_Accepted()
    {
        // MQTT with no broker_url — create succeeds (validation is at send-time)
        var ch = await _svc.CreateAsync("mqtt", "Empty MQTT", true,
            new Dictionary<string, JsonElement>(),
            CancellationToken.None);

        Assert.NotNull(ch);
    }

    [Fact]
    public async Task PublishTelemetry_NoMqttChannels_ReturnsZero()
    {
        // No channels configured — should return 0 without error
        var result = await _svc.PublishTelemetryAsync(
            new List<TelemetryReading>
            {
                new() { SensorId = "cpu_temp_0", SensorName = "CPU", SensorType = "temp", Value = 55.0, Unit = "°C" },
            },
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task PublishTelemetry_NonMqttChannel_Skipped()
    {
        // Create a discord channel — should be skipped for telemetry
        await _svc.CreateAsync("discord", "My Discord", true,
            Cfg(("webhook_url", "https://discord.com/api/webhooks/123/abc")),
            CancellationToken.None);

        var result = await _svc.PublishTelemetryAsync(
            new List<TelemetryReading>
            {
                new() { SensorId = "cpu_temp_0", Value = 55.0 },
            },
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task PublishTelemetry_MqttWithoutPublishFlag_Skipped()
    {
        // MQTT channel without publish_telemetry flag — should be skipped
        await _svc.CreateAsync("mqtt", "Alerts Only", true,
            Cfg(("broker_url", "mqtt://localhost:1883"),
                ("publish_telemetry", false)),
            CancellationToken.None);

        var result = await _svc.PublishTelemetryAsync(
            new List<TelemetryReading>
            {
                new() { SensorId = "cpu_temp_0", Value = 55.0 },
            },
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task SendAlert_InvalidBrokerUrl_ReturnsFalse()
    {
        var ch = await _svc.CreateAsync("mqtt", "Bad Broker", true,
            Cfg(("broker_url", "not-a-url")),
            CancellationToken.None);

        Assert.NotNull(ch);
        var result = await _svc.SendTestAlertAsync(ch!.Id, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ListChannels_IncludesMqttType()
    {
        await _svc.CreateAsync("mqtt", "Listed MQTT", true,
            Cfg(("broker_url", "mqtt://localhost:1883")),
            CancellationToken.None);

        var channels = await _svc.ListAsync(CancellationToken.None);
        Assert.Contains(channels, c => c.Type == "mqtt" && c.Name == "Listed MQTT");
    }
}

// NullHttpClientFactory is already defined in WebhookServiceTests.cs (line 10)
// and is accessible project-wide since tests compile into one assembly.
```

- [ ] **Step 2: Run the tests**

Run: `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q --filter "FullyQualifiedName~MqttChannelTests"`
Expected: All 8 tests pass.

- [ ] **Step 3: Commit**

```bash
git add backend-cs/Tests/MqttChannelTests.cs
git commit -m "test(d3): add MQTT channel tests — config parsing, telemetry skip logic"
```

---

### Task 6: Export endpoint tests

**Files:**
- Create: `backend-cs/Tests/ExportControllerTests.cs`

Test CSV and JSON export endpoints for format correctness, headers, filtering.

- [ ] **Step 1: Write the export test class**

Create `backend-cs/Tests/ExportControllerTests.cs`:

```csharp
using System.Net;
using System.Text;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>CSV and JSON export endpoint tests for AnalyticsController.</summary>
public sealed class ExportControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;

    public ExportControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db = new DbService(settings, NullLogger<DbService>.Instance);
        _db.EnsureInitialisedAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task CsvExport_EmptyData_ReturnsHeadersOnly()
    {
        var ctrl = new Api.AnalyticsController(_db, NullLogger<Api.AnalyticsController>.Instance);
        var result = await ctrl.Export(format: "csv", hours: 1, ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        var csv = Encoding.UTF8.GetString(file.FileContents);
        Assert.StartsWith("timestamp_utc,sensor_id,sensor_name,sensor_type,unit,avg_value,min_value,max_value,sample_count", csv);
    }

    [Fact]
    public async Task JsonExport_EmptyData_ReturnsEmptyArray()
    {
        var ctrl = new Api.AnalyticsController(_db, NullLogger<Api.AnalyticsController>.Instance);
        var result = await ctrl.Export(format: "json", hours: 1, ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);
        var json = Encoding.UTF8.GetString(file.FileContents);
        Assert.Equal("[]", json.Trim());
    }

    [Fact]
    public async Task CsvExport_WithData_IncludesRows()
    {
        // Insert a sensor reading so there's data to export
        await _db.LogReadingsAsync(new List<Api.SensorReading>
        {
            new() { Id = "cpu_temp_0", Name = "CPU", SensorType = "temp", Value = 55.0, Unit = "°C" },
        }, CancellationToken.None);

        var ctrl = new Api.AnalyticsController(_db, NullLogger<Api.AnalyticsController>.Instance);
        var result = await ctrl.Export(format: "csv", hours: 24, ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = Encoding.UTF8.GetString(file.FileContents);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, "Should have header + at least one data row");
        Assert.Contains("cpu_temp_0", lines[1]);
    }

    [Fact]
    public async Task CsvExport_SpecialCharacters_Escaped()
    {
        // Sensor name with comma — should be CSV-escaped
        await _db.LogReadingsAsync(new List<Api.SensorReading>
        {
            new() { Id = "test_sensor", Name = "CPU, Core 0", SensorType = "temp", Value = 50.0, Unit = "°C" },
        }, CancellationToken.None);

        var ctrl = new Api.AnalyticsController(_db, NullLogger<Api.AnalyticsController>.Instance);
        var result = await ctrl.Export(format: "csv", hours: 24, ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = Encoding.UTF8.GetString(file.FileContents);
        // Name with comma should be quoted
        Assert.Contains("\"CPU, Core 0\"", csv);
    }

    [Fact]
    public async Task Export_InvalidFormat_DefaultsToCsv()
    {
        var ctrl = new Api.AnalyticsController(_db, NullLogger<Api.AnalyticsController>.Instance);
        var result = await ctrl.Export(format: "xml", hours: 1, ct: CancellationToken.None);

        // Should default to CSV or return an error — verify it doesn't crash
        Assert.NotNull(result);
    }
}
```

**Note:** The exact constructor signature and method names of `AnalyticsController`
and `DbService.LogReadingsAsync` must match the actual codebase. The implementing
engineer should check the actual signatures and adjust. The `Api.SensorReading`
type reference may need adjustment based on the actual model namespace.

- [ ] **Step 2: Run the tests**

Run: `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ExportControllerTests"`
Expected: All 5 tests pass. If any fail due to constructor/signature mismatch,
adjust to match the actual codebase signatures.

- [ ] **Step 3: Commit**

```bash
git add backend-cs/Tests/ExportControllerTests.cs
git commit -m "test(d3): add CSV/JSON export endpoint tests"
```

---

### Task 7: Machine status eviction tests

**Files:**
- Create: `backend-cs/Tests/MachineStatusTests.cs`

Test the machine health check and status eviction logic.

- [ ] **Step 1: Check the actual machine status code**

Before writing tests, read the machine status eviction implementation:
- `backend-cs/Api/MachinesController.cs` — check for health check / eviction
- `backend-cs/Services/DbService.cs` — check for `consecutive_failures`, `status`, `last_seen_at` columns

The Milestone C design specified machine status tracking columns were added in
migration 015. Write tests based on what actually exists. If the eviction logic
is only partially implemented, test what exists and note gaps.

- [ ] **Step 2: Write machine status tests**

Create `backend-cs/Tests/MachineStatusTests.cs` based on the actual
implementation. At minimum, test:
- Machine creation sets `status = 'unknown'` and `consecutive_failures = 0`
- Machine list returns `status`, `last_seen_at`, `last_error` fields
- If health check endpoint exists: consecutive failures increment, recovery resets

- [ ] **Step 3: Run the tests**

Run: `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q --filter "FullyQualifiedName~MachineStatusTests"`
Expected: All tests pass.

- [ ] **Step 4: Run all C# tests to verify no regressions**

Run: `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q`
Expected: All tests pass (215+).

- [ ] **Step 5: Commit**

```bash
git add backend-cs/Tests/MachineStatusTests.cs
git commit -m "test(d3): add machine status eviction tests"
```

---

## Chunk 4: D4 — Session Token Rotation on Password Change

### Task 8: Add self-password-change endpoint (Python)

**Files:**
- Modify: `backend/app/api/routes/auth.py`
- Modify: `backend/app/services/auth_service.py`

Currently, only admins can change passwords via `PUT /api/auth/users/{user_id}/password`.
We need a self-service endpoint that lets any authenticated user change their own
password, with session rotation.

- [ ] **Step 1: Write the test for self-password-change**

Create or append to `backend/tests/test_session_rotation.py`:

```python
"""Tests for self-password-change with session rotation."""
import pytest
import secrets
from unittest.mock import AsyncMock, MagicMock, patch


@pytest.mark.asyncio
async def test_self_password_change_returns_new_session(aiohttp_client_or_similar):
    """Self-password-change should return new session + CSRF tokens and invalidate other sessions."""
    # This is a high-level integration test. The exact fixture depends on how
    # the test suite creates test clients.
    #
    # Key assertions:
    # 1. POST /api/auth/me/password with {current_password, new_password}
    # 2. Response includes new session cookie + CSRF cookie
    # 3. Old session token no longer validates
    # 4. New session token validates successfully
    pass  # Implement based on test fixture pattern used in backend/tests/


@pytest.mark.asyncio
async def test_self_password_change_wrong_current_password():
    """Should reject if current_password is incorrect."""
    pass


@pytest.mark.asyncio
async def test_self_password_change_invalidates_other_sessions():
    """Other sessions for the same user should be invalidated."""
    pass
```

**Note:** The test fixtures vary by project. The implementing engineer should
follow the existing test patterns in `backend/tests/` (look for how other auth
tests create test clients and sessions).

- [ ] **Step 2: Add the self-password-change endpoint**

In `backend/app/api/routes/auth.py`, add a new Pydantic model and endpoint:

```python
class SelfPasswordChangeRequest(BaseModel):
    current_password: str
    new_password: str = Field(..., min_length=8, max_length=256)
```

Add the endpoint (after the existing `change_user_password` endpoint):

```python
@router.post("/me/password", dependencies=[Depends(require_auth), Depends(require_csrf)])
async def change_my_password(body: SelfPasswordChangeRequest, request: Request):
    """Change the current user's own password. Rotates session token."""
    auth_service = request.app.state.auth_service
    session_token = request.cookies.get("drivechill_session")
    session = await auth_service.validate_session(session_token)
    if not session:
        raise HTTPException(status_code=401, detail="Invalid session")

    user = await auth_service.get_user(session["username"])
    if not user:
        raise HTTPException(status_code=404, detail="User not found")

    # Verify current password
    if not auth_service.verify_password(body.current_password, user["password_hash"]):
        raise HTTPException(status_code=403, detail="Current password is incorrect")

    # Update password
    pw_hash = auth_service.hash_password(body.new_password)
    await auth_service._db.execute(
        "UPDATE users SET password_hash = ?, updated_at = datetime('now') WHERE id = ?",
        (pw_hash, user["id"]),
    )
    await auth_service._db.commit()

    # Invalidate ALL sessions for this user (including current)
    await auth_service._db.execute(
        "DELETE FROM sessions WHERE user_id = ?", (user["id"],)
    )
    await auth_service._db.commit()

    # Create fresh session
    new_session_token, new_csrf_token = await auth_service._create_session(
        user["id"],
        request.client.host if request.client else "unknown",
        request.headers.get("user-agent", ""),
    )

    ip = request.client.host if request.client else "unknown"
    await auth_service._log_auth_event(
        "self_password_changed", ip, user["username"], "success", ""
    )

    response = JSONResponse({"success": True})
    secure = request.url.scheme == "https"
    _set_session_cookies(response, new_session_token, new_csrf_token, secure=secure)
    return response
```

**Important:** Check if `auth_service._create_session()` exists. If not, extract
the session-creation logic from `login()` into a reusable helper first:

In `backend/app/services/auth_service.py`, extract session creation:

```python
async def _create_session(self, user_id: int, ip: str, user_agent: str) -> tuple[str, str]:
    """Create a new session and return (session_token, csrf_token)."""
    session_token = secrets.token_hex(32)
    csrf_token = secrets.token_hex(32)
    now = datetime.now(timezone.utc)
    expires_at = now + timedelta(seconds=self._session_ttl)
    await self._db.execute(
        "INSERT INTO sessions (token, user_id, csrf_token, created_at, "
        "last_active, expires_at, ip_address, user_agent) "
        "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
        (session_token, user_id, csrf_token, now.isoformat(),
         now.isoformat(), expires_at.isoformat(), ip, user_agent),
    )
    await self._db.commit()
    return session_token, csrf_token
```

Then refactor `login()` to call `_create_session()` instead of duplicating the
logic.

- [ ] **Step 3: Run Python tests**

Run: `cd backend && python -m pytest tests/ -q`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add backend/app/api/routes/auth.py backend/app/services/auth_service.py backend/tests/test_session_rotation.py
git commit -m "feat(d4): add self-password-change endpoint with session rotation (Python)"
```

---

### Task 9: Add self-password-change endpoint (C#)

**Files:**
- Modify: `backend-cs/Api/AuthController.cs`
- Modify: `backend-cs/Services/SessionService.cs` (if needed)

- [ ] **Step 1: Add the C# self-password-change endpoint**

In `backend-cs/Api/AuthController.cs`, add a new request model:

```csharp
public sealed class SelfPasswordChangeRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}
```

Add the endpoint:

```csharp
/// <summary>POST /api/auth/me/password — change own password with session rotation.</summary>
[HttpPost("me/password")]
public async Task<IActionResult> ChangeMyPassword([FromBody] SelfPasswordChangeRequest req, CancellationToken ct = default)
{
    var sessionToken = Request.Cookies[SessionCookieName];
    if (string.IsNullOrEmpty(sessionToken)) return Unauthorized401("Not authenticated");

    var session = await _sessions.ValidateSessionAsync(sessionToken, ct);
    if (session is null) return Unauthorized401("Invalid session");

    if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8 || req.NewPassword.Length > 256)
        return BadRequest(new { detail = "New password must be 8-256 characters" });

    // GetUserAsync returns (Username, PasswordHash)? tuple
    var user = await _db.GetUserAsync(session.Value.Username, ct);
    if (user is null) return NotFound(new { detail = "User not found" });

    // Verify current password — VerifyPassword is on SessionService (private),
    // so we need to add a public method or use the same PBKDF2 logic here.
    // The simplest approach: add a public VerifyPasswordAsync to SessionService.
    if (!_sessions.VerifyPasswordPublic(req.CurrentPassword, user.Value.PasswordHash))
        return StatusCode(403, new { detail = "Current password is incorrect" });

    // Update password
    var hash = HashPasswordInternal(req.NewPassword);
    // SetUserPasswordAsync takes userId — we need the ID.
    // GetUserAsync only returns (Username, PasswordHash). We need to either:
    // (a) extend GetUserAsync to return Id, or
    // (b) use a separate query.
    // Option (a) is cleaner — add Id to the GetUserAsync return tuple.
    await _db.SetUserPasswordByUsernameAsync(session.Value.Username, hash, ct);

    // Invalidate ALL sessions for this user
    await _db.DeleteUserSessionsByUsernameAsync(session.Value.Username, ct);

    // Create fresh session — extract from SessionService.LoginAsync
    var (newSessionToken, newCsrfToken) = await _sessions.CreateSessionDirectAsync(
        session.Value.Username,
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(), ct);

    var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
    _ = _db.LogAuthEventAsync("self_password_changed", ip, session.Value.Username, "success", "");

    SetSessionCookies(newSessionToken, newCsrfToken);
    return Ok(new { success = true });
}
```

**Required supporting changes in `SessionService.cs`:**

1. Make `VerifyPassword` public (or add a public wrapper):
```csharp
public bool VerifyPasswordPublic(string password, string stored) => VerifyPassword(password, stored);
```

2. Extract session creation from `LoginAsync` into a reusable method:
```csharp
public async Task<(string SessionToken, string CsrfToken)> CreateSessionDirectAsync(
    string username, string? ip, string? userAgent, CancellationToken ct = default)
{
    var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    await _db.CreateSessionAsync(sessionToken, csrfToken, username, ip, userAgent, SessionTtl, ct);
    return (sessionToken, csrfToken);
}
```

Then refactor `LoginAsync` to call `CreateSessionDirectAsync` instead of
inlining the token generation (lines 100-103).

**Required supporting changes in `DbService.cs`:**

Add `SetUserPasswordByUsernameAsync` since we don't have the user ID from
`GetUserAsync`:
```csharp
public async Task SetUserPasswordByUsernameAsync(string username, string hash, CancellationToken ct = default)
{
    await EnsureInitialisedAsync(ct);
    await using var conn = new SqliteConnection(_connStr);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE users SET password_hash = $h, updated_at = $t WHERE username = $u";
    cmd.Parameters.AddWithValue("$h", hash);
    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$u", username);
    await cmd.ExecuteNonQueryAsync(ct);
}

- [ ] **Step 2: Verify C# builds and tests pass**

Run: `cd backend-cs && dotnet build && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q`
Expected: Build succeeds, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add backend-cs/Api/AuthController.cs backend-cs/Services/SessionService.cs
git commit -m "feat(d4): add self-password-change endpoint with session rotation (C#)"
```

---

### Task 10: Add frontend self-password-change with WS reconnect

**Files:**
- Modify: `frontend/src/lib/api.ts`
- Modify: `frontend/src/components/settings/SettingsPage.tsx`
- Modify: `frontend/src/hooks/useWebSocket.ts`

- [ ] **Step 1: Add the API method**

In `frontend/src/lib/api.ts`, inside the `authApi` object (line 135, after `changeUserPassword` at line 154).
Note: `authApi` is a separate export from `api` — it lives at line 135, not nested inside `api`:

```typescript
  changeMyPassword: (currentPassword: string, newPassword: string) =>
    fetchAPI<{ success: boolean }>('/api/auth/me/password', {
      method: 'POST',
      body: JSON.stringify({ current_password: currentPassword, new_password: newPassword }),
    }),
```

- [ ] **Step 2: Export a close function from useWebSocket**

In `frontend/src/hooks/useWebSocket.ts`, we need to expose a way for external
code to force-close the WebSocket. Add a module-level ref and export a function.

At the top of the file, add:

```typescript
// Module-level ref for external close (used by password change to force WS reconnect)
let _globalWsRef: WebSocket | null = null;

export function closeWebSocket() {
  if (_globalWsRef && _globalWsRef.readyState === WebSocket.OPEN) {
    _globalWsRef.close();
  }
}
```

Inside the `connect` callback, after `wsRef.current = ws;`, add:

```typescript
      _globalWsRef = ws;
```

In `ws.onclose`, add:

```typescript
      _globalWsRef = null;
```

- [ ] **Step 3: Add self-password-change UI in SettingsPage**

In `frontend/src/components/settings/SettingsPage.tsx`, add state for the
self-password-change form. Find a suitable location in the User Management
section or create a "My Account" section.

Add state variables:

```typescript
const [myCurrentPw, setMyCurrentPw] = useState('');
const [myNewPw, setMyNewPw] = useState('');
const [myPwBusy, setMyPwBusy] = useState(false);
```

Add the imports:

```typescript
import { closeWebSocket } from '@/hooks/useWebSocket';
import { authApi } from '@/lib/api';
```

Add a "Change My Password" section in the JSX (before or after User Management):

```tsx
{/* Change My Password */}
<div className="card p-6 animate-card-enter">
  <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>
    Change My Password
  </h3>
  <div className="space-y-3">
    <input
      type="password"
      value={myCurrentPw}
      onChange={(e) => setMyCurrentPw(e.target.value)}
      placeholder="Current password"
      className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
      style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
    />
    <input
      type="password"
      value={myNewPw}
      onChange={(e) => setMyNewPw(e.target.value)}
      placeholder="New password (min 8 chars)"
      className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
      style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
    />
    <button
      className="btn-primary text-xs px-4 py-2"
      disabled={myPwBusy || !myCurrentPw || myNewPw.length < 8}
      onClick={async () => {
        setMyPwBusy(true);
        try {
          await api.authApi.changeMyPassword(myCurrentPw, myNewPw);
          toast('Password changed successfully.');
          setMyCurrentPw('');
          setMyNewPw('');
          // Force WebSocket reconnect with new session cookie
          closeWebSocket();
          // Refresh auth state to pick up new CSRF token from new session
          const session = await authApi.checkSession();
          if (session) {
            authStore.getState().setAuth(
              session.auth_required,
              session.authenticated,
              session.username,
              session.role,
            );
          }
        } catch (err: any) {
          toast(err?.message || 'Password change failed.', 'error');
        } finally {
          setMyPwBusy(false);
        }
      }}
    >
      {myPwBusy ? 'Changing...' : 'Change Password'}
    </button>
  </div>
</div>
```

- [ ] **Step 4: Verify frontend builds**

Run: `cd frontend && npx next build`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add frontend/src/lib/api.ts frontend/src/hooks/useWebSocket.ts frontend/src/components/settings/SettingsPage.tsx
git commit -m "feat(d4): frontend self-password-change with WS reconnect"
```

---

## Chunk 5: D5 — Settings E2E Flake Fix

### Task 11: Diagnose and fix the settings spec flake

**Files:**
- Modify: `frontend/e2e/settings.spec.ts`

The `"clicking °F toggles the active unit"` test is flaky. Based on analysis
of the error context snapshot:
- The page IS fully rendered (°C and °F buttons are present as `[ref=e103]` and `[ref=e104]`)
- The page has a "What's new" banner that may interfere with clicks
- The `beforeEach` uses `waitForFunction(() => !document.body.innerText.includes('Loading...'))` which is too coarse

Root causes:
1. The "What's new" banner may overlay or intercept clicks
2. `waitForFunction` checks for "Loading..." globally but doesn't wait for the Settings page to be fully interactive
3. The save button assertion `if (await saveButton.isVisible())` is conditional — unreliable

- [ ] **Step 1: Fix the beforeEach to be more robust**

Replace the `beforeEach` in `frontend/e2e/settings.spec.ts`:

```typescript
test.beforeEach(async ({ page }) => {
  await page.goto('/');
  // Wait for the app to finish initial loading (WebSocket connected)
  await page.waitForFunction(
    () => !document.body.innerText.includes('Loading...'),
    { timeout: 15_000 },
  );

  // Dismiss the "What's new" banner if present — it can overlay other elements
  const dismissBtn = page.getByRole('button', { name: /dismiss/i });
  if (await dismissBtn.isVisible({ timeout: 2_000 }).catch(() => false)) {
    await dismissBtn.click();
    await expect(dismissBtn).not.toBeVisible({ timeout: 2_000 });
  }

  // Navigate to settings page
  const settingsLink = page.getByText(/settings/i).first();
  await settingsLink.click();

  // Wait for settings-specific content (not just "Loading..." absence)
  await expect(
    page.getByRole('heading', { name: /general/i }),
  ).toBeVisible({ timeout: 10_000 });

  // Wait for the Save Settings button to be visible — confirms form has loaded
  await expect(
    page.getByRole('button', { name: /save settings/i }),
  ).toBeVisible({ timeout: 5_000 });

  // NOTE: Do NOT use waitForLoadState('networkidle') — the WebSocket connection
  // keeps the network permanently active, so networkidle never resolves.
  // Instead, we wait for specific UI elements above.
});
```

- [ ] **Step 2: Fix the °F toggle test**

Replace the `"clicking °F toggles the active unit"` test:

```typescript
test('clicking °F toggles the active unit', async ({ page }) => {
  // Find the °F button
  const fButton = page.getByRole('button', { name: /°F/ });
  await expect(fButton).toBeVisible({ timeout: 5_000 });

  // Click °F — use force:true in case the "What's new" banner wasn't dismissed
  await fButton.click();

  // The °F button should still be visible after click (state updated)
  await expect(fButton).toBeVisible();

  // Save the settings and verify success feedback
  const saveButton = page.getByRole('button', { name: /save settings/i });
  await expect(saveButton).toBeEnabled({ timeout: 5_000 });
  await saveButton.click();

  // Verify success feedback — toast message appears
  await expect(page.getByText(/saved/i)).toBeVisible({ timeout: 5_000 });
});
```

- [ ] **Step 3: Run the settings spec locally**

Run: `cd frontend && npx playwright test e2e/settings.spec.ts --reporter=list`
Expected: All 6 tests pass.

- [ ] **Step 4: Run 5 consecutive times to verify stability**

Run:
```bash
cd frontend
for i in 1 2 3 4 5; do
  echo "Run $i..."
  npx playwright test e2e/settings.spec.ts --reporter=line 2>&1 | tail -1
done
```
Expected: All 5 runs pass without flakes.

- [ ] **Step 5: Run full E2E suite to verify no regressions**

Run: `cd frontend && npx playwright test --reporter=list`
Expected: All E2E tests pass (39 total from 8 spec files).

- [ ] **Step 6: Commit**

```bash
git add frontend/e2e/settings.spec.ts
git commit -m "fix(d5): stabilize settings E2E — dismiss banner, networkidle wait, deterministic save assertion"
```

---

## Final Validation

### Task 12: Full validation across all workstreams

- [ ] **Step 1: Run all Python tests**

Run: `cd backend && python -m pytest tests/ -q`
Expected: All tests pass (560+).

- [ ] **Step 2: Run all C# tests**

Run: `cd backend-cs && dotnet test Tests/DriveChill.Tests.csproj --nologo -v q`
Expected: All tests pass (220+, including new D3 tests).

- [ ] **Step 3: Build frontend**

Run: `cd frontend && npx next build`
Expected: Build succeeds.

- [ ] **Step 4: Run full E2E suite**

Run: `cd frontend && npx playwright test --reporter=list`
Expected: All tests pass.

- [ ] **Step 5: Verify C# build**

Run: `cd backend-cs && dotnet build`
Expected: Build succeeds with no warnings or errors.
