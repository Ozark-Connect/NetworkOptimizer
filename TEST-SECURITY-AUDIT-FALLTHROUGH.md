# Security Audit Fallthrough Logic Test Plan

## Objective
Validate that the IKEA Dirigera hub is detected as an IoT device via fallthrough logic (MAC OUI) and flagged if not on the IoT VLAN.

## Current Setup
- **Device:** IKEA Dirigera (Smart Home Hub)
- **Port Name:** Removed (no custom name)
- **Current VLAN:** IoT VLAN ✓
- **Expected Detection:** MAC OUI → SmartLighting/SmartHub category

## Gap Found: UniFi Oui Field Not Used!

The `UniFiClientResponse.Oui` field contains the **manufacturer name** (e.g., "IKEA of Sweden") but we're not using it for detection. This is a missed opportunity!

### Current Detection Sources
1. `dev_cat`/`dev_vendor` - UniFi fingerprint IDs (maps to database)
2. Our MAC OUI dictionary - Hardcoded MAC prefixes (may be incomplete)
3. Name patterns - Hostname/port name matching

### Missing Detection Source
- **`Oui` field** - UniFi's resolved manufacturer name (e.g., "IKEA of Sweden", "Philips Lighting")

### Proposed Enhancement
Add `UniFiOuiDetector` that matches manufacturer names:
```csharp
// In DeviceTypeDetectionService
if (!string.IsNullOrEmpty(client?.Oui))
{
    var ouiName = client.Oui.ToLowerInvariant();
    if (ouiName.Contains("ikea")) return SmartLighting;
    if (ouiName.Contains("philips lighting")) return SmartLighting;
    if (ouiName.Contains("ring")) return SecurityCamera;
    // etc.
}
```

## Detection Chain (Expected)

```
1. UniFi Fingerprint → Likely no match (IKEA not in fingerprint DB)
2. UniFi Oui Name → NEW! Match "IKEA of Sweden" → SmartHub
3. MAC OUI Lookup → Match! IKEA OUIs: 94:54:93, D0:CF:5E, CC:50:E3
4. Name Pattern → May match "dirigera" if hostname contains it
```

## Test Scenarios

### Test 1: Verify Detection Works (Current State)
**Goal:** Confirm Dirigera is detected as IoT device

1. Run Security Audit from the UI
2. Check audit results for the Dirigera's port
3. Verify detection metadata shows:
   - Category: `SmartLighting` or `SmartHub`
   - Source: `MacOui` (not `PortName`)
   - Confidence: ~75-85%
4. Should show **NO issue** (already on IoT VLAN)

### Test 2: Simulate Wrong VLAN (Temporary Change)
**Goal:** Confirm audit flags IoT device on wrong VLAN

**Option A - Change VLAN in UniFi (safest):**
1. In UniFi UI, change the port's VLAN to "Default" or "LAN"
2. Wait for controller sync (~30 seconds)
3. Run Security Audit
4. **Expected:** Critical issue `IOT-VLAN-001` raised
5. Verify issue details:
   - Device type detected
   - Recommended VLAN shown
   - Detection confidence included
6. Revert VLAN to IoT

**Option B - Temporarily move device:**
1. Unplug Dirigera from IoT switch port
2. Plug into a Default VLAN port
3. Run Security Audit
4. Verify issue raised
5. Move back

### Test 3: Verify MAC OUI is Primary Detection
**Goal:** Confirm we're not relying on port name

1. Confirm port name is blank/generic
2. Run audit
3. Check detection source in results:
   - Should be `MacOui` NOT `PortName`
4. If detection source shows `PortName`, the fallthrough isn't being tested

## Commands for Testing

### Check Dirigera's MAC on gateway:
```bash
ssh root@unifi "cat /proc/net/arp | grep -i '94:54:93\|d0:cf:5e\|cc:50:e3'"
```

### Check current VLAN assignment:
```bash
# Via UniFi API - check client's network assignment
```

### View audit logs:
```bash
ssh root@nas "docker logs network-optimizer 2>&1 | grep -i dirigera"
```

## Expected Audit Issue (When on Wrong VLAN)

```json
{
  "ruleId": "IOT-VLAN-001",
  "severity": "Critical",
  "title": "IoT device not on isolated VLAN",
  "description": "Device 'IKEA Dirigera' detected as SmartHub is on Default network...",
  "metadata": {
    "deviceType": "SmartHub",
    "detectionSource": "MacOui",
    "confidence": 85,
    "vendor": "IKEA",
    "currentNetwork": "Default",
    "recommendedNetwork": "IoT"
  }
}
```

## Validation Checklist

- [ ] Dirigera detected without port name (fallthrough works)
- [ ] Detection source is `MacOui` not `PortName`
- [ ] No false positive when on IoT VLAN
- [ ] Critical issue raised when on wrong VLAN
- [ ] Issue includes helpful remediation info
- [ ] Detection confidence is reasonable (70%+)

## Notes

- IKEA OUIs in detector: `94:54:93`, `D0:CF:5E`, `CC:50:E3`
- Mapped to `SmartLighting` category (could also be `SmartHub`)
- IoT VLAN rule checks for `NetworkPurpose.IoT` or `NetworkPurpose.Security`

## After Testing

1. Ensure Dirigera is back on IoT VLAN
2. Delete this file: `git rm TEST-SECURITY-AUDIT-FALLTHROUGH.md`
