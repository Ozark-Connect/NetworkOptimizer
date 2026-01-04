# OpenSpeedTest with saveData configured to send results to Network Optimizer
FROM openspeedtest/latest

# Copy our patch script
COPY entrypoint.sh /patch-savedata.sh
RUN chmod +x /patch-savedata.sh

# Run our patch script, then the original entrypoint
ENTRYPOINT ["/bin/sh", "-c", "/patch-savedata.sh"]
