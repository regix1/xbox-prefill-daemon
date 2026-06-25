FROM ubuntu:20.04
LABEL maintainers="regix1"

ARG TARGETARCH

RUN \
	apt update \
	&& DEBIAN_FRONTEND=noninteractive apt install -y --no-install-recommends \
		ca-certificates \
		curl \
		dnsutils \
		libncursesw5 \
		locales \
		tzdata \
	&& sed -i '/en_US.UTF-8/s/^# //' /etc/locale.gen \
	&& dpkg-reconfigure --frontend=noninteractive locales \
	&& update-locale LANG=en_US.UTF-8 \
	&& rm -rf /var/cache/apt/archives /var/lib/apt/lists/*

ENV \
	LANG=en_US.UTF-8 \
	LANGUAGE=en_US:en \
	LC_ALL=en_US.UTF-8 \
	TERM=xterm-256color \
	HOME=/app

# Create a non-root user and group for running the daemon
RUN groupadd --gid 1000 xboxprefill \
    && useradd --uid 1000 --gid xboxprefill --no-create-home --shell /bin/false xboxprefill

# Create app directory structure
WORKDIR /app

# Create directories for daemon mode and config persistence.
# Socket dir and Config get restrictive permissions (0700); the daemon enforces 0600 on the socket
# file itself after bind. /app/.cache is app-private (0700). /commands and /responses remain
# accessible only to the daemon user.
RUN mkdir -p /commands /responses /app/Config /app/.cache \
    && chown -R xboxprefill:xboxprefill /app /commands /responses \
    && chmod 700 /app/Config /app/.cache

# Copy architecture-specific binary
# TARGETARCH is automatically set by docker buildx (amd64, arm64, etc.)
COPY /publish/${TARGETARCH}/XboxPrefill /app/XboxPrefill
RUN chmod +x /app/XboxPrefill \
    && chown xboxprefill:xboxprefill /app/XboxPrefill

# Drop root — all daemon operations run as the unprivileged xboxprefill user
USER xboxprefill

# Volumes for persistence and daemon communication
VOLUME ["/commands", "/responses", "/app/Config", "/app/.cache"]

ENTRYPOINT [ "/app/XboxPrefill" ]
