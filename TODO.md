# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### Retransmit Analysis
- Flag high retransmit counts as a separate insight
- Example: "High packet loss detected on return path" when retransmits are asymmetric
- Observation: Front Yard AP test showed 3181 retransmits "to device" but 0 "from device"
- This asymmetry on wireless mesh links makes sense (mesh uplink contention)

### Path Analysis Enhancements
- Direction-aware bottleneck calculation (separate from/to max speeds)
- More gateway models in routing limits table as we gather data
- Threshold tuning based on real-world data collection

## General

- (Add future enhancements here)
