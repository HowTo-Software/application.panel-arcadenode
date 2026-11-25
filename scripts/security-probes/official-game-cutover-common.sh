#!/bin/bash

require_local_game_runner() {
    local image="$1" tool
    [[ "$(uname -s)" == Linux && "$image" =~ ^[a-zA-Z0-9][a-zA-Z0-9._:/@-]+$ ]]
    unset DOCKER_CONTEXT
    export DOCKER_HOST=unix:///var/run/docker.sock PTERODACTYL_DOCKER_SOCKET=/var/run/docker.sock
    for tool in docker dotnet jq python3 rsync setpriv sudo timeout; do command -v "$tool" >/dev/null; done
    sudo -n true
    test "$(docker image inspect --format '{{index .Config.Labels "arcadenode.restore"}}' "$image")" = 1
}

publish_cutover_preflight() {
    dotnet publish scripts/pterodactyl-migration/Preflight.csproj -c Release -r linux-x64 --self-contained true -o "$1" -warnaserror
}

send_console() {
    # Exercise the TTY stdin upgrade through the disposable runner's local Docker socket.
    python3 - "$1" "${@:2}" <<'PY'
import re
import socket
import sys

container, *commands = sys.argv[1:]
if not re.fullmatch(r"[0-9a-f]{64}", container) or not commands:
    raise ValueError("Expected the ID of the disposable game container and console input")
path = f"/containers/{container}/attach?stream=1&stdin=1"
request = (f"POST {path} HTTP/1.1\r\nHost: localhost\r\n"
           "Connection: Upgrade\r\nUpgrade: tcp\r\nContent-Length: 0\r\n\r\n")
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as stream:
    stream.settimeout(8)
    stream.connect("/var/run/docker.sock")
    stream.sendall(request.encode("ascii"))
    response = bytearray()
    while b"\r\n\r\n" not in response:
        chunk = stream.recv(1024)
        if not chunk or len(response) + len(chunk) > 8192:
            raise RuntimeError("Incomplete Docker attach response")
        response.extend(chunk)
    header = bytes(response).split(b"\r\n\r\n", 1)[0].split(b"\r\n")
    fields = {name.lower(): value.strip().lower() for name, _, value in
              (line.partition(b":") for line in header[1:])}
    if header[0].split()[1:2] != [b"101"] or fields.get(b"upgrade") != b"tcp":
        raise RuntimeError("Docker rejected the console upgrade")
    stream.sendall(("\n".join(commands) + "\n").encode("utf-8"))
PY
}

wait_for_log() {
    local container="$1" pattern="$2" seconds="$3"
    local end=$((SECONDS + seconds)) output
    while (( SECONDS < end )); do
        output="$(docker logs --tail 120 "$container" 2>&1)"
        if grep -Eq "$pattern" <<< "$output"; then return 0; fi
        if [[ "$(docker inspect --format '{{.State.Running}}' "$container")" != true ]]; then break; fi
        sleep 2
    done
    echo "Expected game output not observed: $pattern" >&2
    return 1
}

wait_for_clean_exit() {
    test "$(timeout --kill-after=5 150 docker wait "$1")" = 0
}
