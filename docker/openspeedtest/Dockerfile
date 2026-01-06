# Ozark Connect Speed Test - based on OpenSpeedTest
# Built from local customized source instead of upstream image
FROM nginx:alpine

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
