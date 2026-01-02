# Speed Test & Network Trace - Future Enhancements

## Current State (Jan 2026)
- Network path visualization with device icons
- Wireless link indicators on mesh hops
- Bottleneck detection and highlighting
- Efficiency grades (% of theoretical max)
- Inter-VLAN routing detection
- LocalIp parsing from iperf3 for accurate server positioning
- Path analysis persistence as snapshot
- Test history with expandable details

## Proposed Enhancements

### Low Effort / High Impact

**1. Latency & Jitter Display**
- iperf3 already outputs latency/jitter data - just need to parse and display
- Critical for VoIP, video conferencing, gaming users
- Show alongside throughput in results

**2. Bottleneck Recommendations**
- We already detect bottlenecks - add actionable suggestions
- Examples:
  - "Upgrade 1G link to 2.5G for estimated 2.5x improvement"
  - "Wireless mesh is limiting - consider wired backhaul"
  - "This switch port supports 2.5G but is negotiating at 1G"

**3. Wireless Signal Quality on Path**
- Show RSSI/SNR for wireless hops (data available from UniFi API)
- Correlate signal quality with throughput
- Help identify weak wireless links

### Medium Effort / High Impact

**4. Scheduled Tests + Trend Graphs**
- Configure recurring tests (e.g., every 6 hours)
- Track performance over time per device/path
- Answer: "Is my AP degrading over time?"
- Detect intermittent issues

**5. Performance Alerts**
- Threshold-based notifications
- "Alert me if throughput drops below 500 Mbps"
- "Alert if efficiency drops below 70%"
- Integration with notification system

**6. PDF Export of Path Analysis**
- Export current path analysis to PDF
- Useful for documentation, client reports, troubleshooting
- Leverage existing PDF infrastructure

### Higher Effort / Differentiating

**7. Live Path Monitoring**
- Real-time visualization during test execution
- Show per-second throughput on each hop
- Animated data flow through path

**8. Multi-Device Comparison**
- Side-by-side comparison of multiple devices/paths
- "Which AP has the best backhaul?"
- Identify network-wide patterns

**9. Historical Path Change Detection**
- Detect when network topology changes
- "Your path now goes through an additional switch"
- Track infrastructure changes over time

## Notes
- Architecture: Network Optimizer acts as iperf3 client, SSHs to target to start iperf3 server
- Path analysis uses LocalIp from iperf3 output for accurate server detection
- Device icons resolved via UniFi product database shortname lookup
