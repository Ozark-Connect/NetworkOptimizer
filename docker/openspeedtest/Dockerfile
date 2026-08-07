# Ozark Connect Speed Test - based on OpenSpeedTest
# Built from local customized source instead of upstream image
# Track the 1.30 stable line; the floating minor tag picks up patch releases.
# Revisit when 1.30 goes legacy - a pinned EOL branch stops getting base-image
# rebuilds, so the whole Alpine layer goes stale with it.
FROM nginx:1.30-alpine

# Copy our customized OpenSpeedTest files
COPY src/OpenSpeedTest/ /usr/share/nginx/html/

# Copy nginx configuration
COPY docker/openspeedtest/nginx.conf /etc/nginx/conf.d/default.conf

# Copy entrypoint script
COPY docker/openspeedtest/entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

EXPOSE 3000

ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]
