FROM ubuntu:20.04
LABEL maintainers="regix1"

ARG TARGETARCH

RUN \
	apt update \
	&& DEBIAN_FRONTEND=noninteractive apt install -y --no-install-recommends \
		ca-certificates \
		curl \
		dnsutils \
		gosu \
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

# Create a non-root user and group for running the daemon.
# Home is /app (where the app + its Config live) so that gosu, which derives $HOME from
# the user's passwd entry when dropping privileges, lands on /app rather than a
# non-existent /home/xboxprefill (which the unprivileged user cannot create, and which
# .NET would otherwise use for LocalApplicationData / ~/.config).
RUN groupadd --gid 1000 xboxprefill \
    && useradd --uid 1000 --gid xboxprefill --no-create-home --home-dir /app --shell /bin/false xboxprefill

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

# Entrypoint runs as root only long enough to take ownership of the bind-mounted IPC
# directories (/commands, /responses), then drops to the unprivileged xboxprefill user
# via gosu before exec'ing the daemon. We intentionally do NOT set `USER xboxprefill`
# here: the manager bind-mounts host dirs over /commands and /responses as root, which
# overrides the build-time chown, so a non-root entrypoint could not bind the socket.
# Dropping privileges at runtime (after the mounts exist) keeps the daemon unprivileged
# while still being able to create /responses/daemon.sock.
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 755 /usr/local/bin/docker-entrypoint.sh

# Volumes for persistence and daemon communication
VOLUME ["/commands", "/responses", "/app/Config", "/app/.cache"]

ENTRYPOINT [ "/usr/local/bin/docker-entrypoint.sh" ]
