# OpenSpeedTest with saveData configured to send results to Network Optimizer
FROM openspeedtest/latest

# Copy our patch script (use --chmod to set executable during copy)
COPY --chmod=755 entrypoint.sh /patch-savedata.sh

# Run our patch script, then the original entrypoint
ENTRYPOINT ["/bin/sh", "/patch-savedata.sh"]
