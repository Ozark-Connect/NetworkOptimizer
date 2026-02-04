# Spectrum Analysis UI Implementation Plan

## Overview

Build a new "Spectrum" tab in the WiFi Optimizer to display RF environment/spectrum scan data from UniFi APs. This provides visibility into neighboring networks, channel utilization, and DFS channel availability.

## Tab Placement

Insert "Spectrum" as the **4th tab** in the WiFi Optimizer navigation, between "Channels" and "Roaming":

```
Overview | Clients | Channels | Spectrum | Roaming | Band Steering | Airtime | Load Balance
```

This placement makes sense because:
- Spectrum data is closely related to channel analysis (both deal with RF environment)
- Users analyzing channel interference naturally want to see what's in the RF environment
- Keeps the logical flow: Overview → Clients → Channels → Spectrum (RF details)

## Data Source

**Existing backend infrastructure:**
- `ChannelScanResult` model in `NetworkOptimizer.WiFi/Models/`
- `GetChannelScanResultsAsync()` in `UniFiLiveDataProvider`
- Data comes from `scan_radio_table` / `spectrum_table` on device API responses

**Key data fields from ChannelScanResult:**
- `AccessPointMac` - Which AP performed the scan
- `Band` - RadioBand (2.4/5/6 GHz)
- `Channel` - Channel number
- `Width` - Channel width
- `Bssid` - Neighboring network BSSID
- `Ssid` - Neighboring network SSID (may be hidden)
- `Rssi` - Signal strength
- `IsDfs` - Whether this is a DFS channel
- `LastSeen` - When the network was last detected

## Component Structure

### New File: `SpectrumAnalysis.razor`

Location: `src/NetworkOptimizer.Web/Components/Shared/WiFi/SpectrumAnalysis.razor`

### UI Sections

1. **AP Selector & Band Filter** (top)
   - Dropdown to select specific AP or "All APs"
   - Band filter tabs: All | 2.4 GHz | 5 GHz | 6 GHz
   - Refresh button with loading state

2. **Spectrum Summary Cards** (stat row)
   - Total Networks Detected
   - Networks on Your Channels (potential interference)
   - Available DFS Channels
   - Cleanest Channel recommendation

3. **Channel Heatmap** (visual)
   - Horizontal bar chart showing network count per channel
   - Color intensity based on congestion level
   - Your AP channels highlighted with marker
   - DFS channels indicated with icon

4. **Neighboring Networks Table**
   - Columns: SSID | BSSID | Channel | Width | RSSI | AP That Detected | Last Seen
   - Sortable by any column
   - Filter by channel or signal strength
   - Hidden SSIDs shown as "(Hidden)"
   - Group by channel option

5. **DFS Channel Status** (card)
   - List of DFS channels with availability status
   - Show if any radar events detected (if data available)
   - Indicate which DFS channels are in use by your APs

6. **Recommendations** (bottom)
   - Suggest less congested channels based on scan data
   - Warn about heavily used channels
   - Note DFS channel opportunities

## CSS Styling

Follow existing patterns from other WiFi components:
- Use `.spectrum-container` as root class
- Card-based layout with `.spectrum-card`
- Standard stat row with `.spectrum-stats-row` and `.spectrum-stat-card`
- Filter tabs matching `.filter-tabs` pattern
- Table styling matching existing tables
- Loading/empty states matching other components

## Implementation Steps

### Step 1: Create Component Shell
- Create `SpectrumAnalysis.razor` with basic structure
- Add loading state, empty state, error handling
- Wire up to `WiFiOptimizerService`

### Step 2: Add Service Method
- Add `GetChannelScanResultsAsync()` to `WiFiOptimizerService` with caching
- Similar pattern to other cached data methods

### Step 3: Build Summary Stats
- Calculate total networks, networks on same channels as APs
- Identify available DFS channels
- Determine cleanest channel per band

### Step 4: Build Channel Heatmap
- Create horizontal bar visualization
- Show network count per channel
- Highlight your AP channels
- Mark DFS channels

### Step 5: Build Neighboring Networks Table
- Sortable columns
- Filtering capabilities
- Handle hidden SSIDs gracefully

### Step 6: Build DFS Status Section
- Show DFS channel availability
- List which of your APs use DFS

### Step 7: Generate Recommendations
- Analyze scan data for congestion patterns
- Suggest channel changes if beneficial
- Note DFS opportunities

### Step 8: Add Tab Navigation
- Update `WiFiOptimizerPage.razor` to include Spectrum tab
- Position after Channels tab

### Step 9: CSS Styling
- Add scoped CSS following existing patterns
- Ensure responsive layout for mobile

## Dependencies

- `WiFiOptimizerService` - add caching wrapper for spectrum data
- `AccessPointSnapshot` - correlate which channels your APs use
- Existing `DeviceIcon` component for AP icons in table

## Estimated Scope

- 1 new Razor component (~400-500 lines)
- 1 service method addition (~20 lines)
- CSS styling (~200 lines)
- Tab navigation update (~5 lines)
