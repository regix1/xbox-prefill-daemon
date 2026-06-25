#!/bin/sh
# Entrypoint for the Xbox prefill daemon.
#
# The daemon runs as the unprivileged "xboxprefill" user (uid/gid 1000) for security
# hardening. The manager bind-mounts host directories over /commands and /responses;
# a bind mount overrides any ownership the image set on those paths at build time, so
# inside the running container they are owned by whoever created them on the host
# (root, because the manager runs as root). The non-root daemon then cannot create or
# bind its Unix socket at /responses/daemon.sock and dies with "Permission denied".
#
# To fix that without giving up the non-root daemon, this entrypoint runs briefly as
# root, takes ownership of the mounted IPC directories, then drops privileges with
# gosu and execs the daemon as xboxprefill. If we are somehow already running as a
# non-root user (e.g. the container was started with --user), we skip the chown/gosu
# and just exec the daemon directly.
set -e

APP_USER=xboxprefill
APP_GROUP=xboxprefill

if [ "$(id -u)" = "0" ]; then
    # Take ownership of the bind-mounted IPC dirs so the unprivileged daemon can bind
    # its socket and read/write commands. Only chown what exists; tolerate failures on
    # read-only mounts rather than crashing the container.
    for dir in /commands /responses; do
        if [ -d "$dir" ]; then
            chown "$APP_USER:$APP_GROUP" "$dir" 2>/dev/null || true
            chmod 700 "$dir" 2>/dev/null || true
        fi
    done

    # Force HOME=/app for the dropped-privilege process. gosu derives $HOME from the
    # target user's passwd entry; we set that to /app at build time, but we pin it here
    # too so .NET's LocalApplicationData / ~/.config never resolves to a path the
    # unprivileged user cannot create.
    exec gosu "$APP_USER" env HOME=/app /app/XboxPrefill "$@"
fi

# Already non-root (started with --user): just run the daemon.
exec /app/XboxPrefill "$@"
