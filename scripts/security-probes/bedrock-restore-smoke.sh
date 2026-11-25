#!/bin/bash
set -euo pipefail

temp_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
scratch="$(mktemp -d "$temp_root/arcadenode-bedrock-restore.XXXXXX")"
[[ "$scratch" == "$temp_root"/arcadenode-bedrock-restore.* ]]
trap 'rm -rf -- "$scratch"' EXIT

sed 's|^BEDROCK_HOME="/home/bedrock"$|BEDROCK_HOME="$TEST_BEDROCK_HOME"|' \
    game-servers/minecraft-bedrock/entrypoint.sh > "$scratch/entrypoint.sh"
cat > "$scratch/launcher" <<'LAUNCHER'
#!/bin/bash
printf '%s\n' "$PWD" "$LD_LIBRARY_PATH" > "$TEST_BEDROCK_HOME/launch"
LAUNCHER

export ARCADENODE_RESTORED_VOLUME=true SERVER_PORT=19132 SERVER_PORTV6=19133
for layout in pterodactyl managed; do
    export TEST_BEDROCK_HOME="$scratch/$layout"
    installation="$TEST_BEDROCK_HOME"
    if [[ "$layout" == managed ]]; then installation="$installation/server"; fi
    worlds="$installation/worlds"
    if [[ "$layout" == managed && "$(uname -s)" == Linux ]]; then
        worlds="$TEST_BEDROCK_HOME/data/worlds"
        mkdir -p "$installation" "$worlds"
        ln -s ../data/worlds "$installation/worlds"
    fi
    mkdir -p "$worlds/Existing world/db"
    printf '%s\n' 'persisted world data' > "$worlds/Existing world/db/CURRENT"
    cp "$scratch/launcher" "$installation/bedrock_server"
    chmod 700 "$installation/bedrock_server"
    config="$installation/server.properties"
    printf '%s\n' 'server-port=19132' 'server-portv6=19133' 'level-name=Existing world' > "$config"
    cp "$config" "$scratch/expected-config"

    if SERVER_PORTV6=19134 bash "$scratch/entrypoint.sh" > "$scratch/rejected" 2>&1; then
        echo 'Bedrock launched with a conflicting port.' >&2
        exit 1
    fi
    grep -Fq 'ports differ from the approved listener' "$scratch/rejected"
    test ! -e "$TEST_BEDROCK_HOME/launch"
    bash "$scratch/entrypoint.sh" >/dev/null
    printf '%s\n' "$installation" "$installation" > "$scratch/expected-launch"
    cmp "$scratch/expected-launch" "$TEST_BEDROCK_HOME/launch"
    cmp "$scratch/expected-config" "$config"
    grep -Fxq 'persisted world data' "$worlds/Existing world/db/CURRENT"
done
