#!/bin/bash
set -e
umask 027
MINECRAFT_HOME="/home/minecraft"
SERVER_DIR="${MINECRAFT_HOME}/server"
DATA_DIR="${MINECRAFT_HOME}/data"
PLUGINS_DIR="${MINECRAFT_HOME}/plugins"
server_jar_name="${SERVER_JAR:-server.jar}"
if [[ ! "$server_jar_name" =~ ^[A-Za-z0-9._-]+\.jar$ || "${#server_jar_name}" -gt 200 ]]; then
    echo '[ERROR] SERVER_JAR must be a JAR filename with no directory.' >&2
    exit 1
fi

select_java() {
    case "$1" in
        auto|21) java_bin=java ;;
        25) java_bin=/opt/java/java25/bin/java ;;
        *) echo '[ERROR] JAVA_RUNTIME must be auto, 21 or 25.' >&2; exit 1 ;;
    esac
}
select_java "${JAVA_RUNTIME:-auto}"

managed_heap="${ARCADENODE_JAVA_HEAP_MIB:-}"
java_heap=(-XX:MaxRAMPercentage=75.0)
if [[ -n "$managed_heap" ]]; then
    if [[ ! "$managed_heap" =~ ^[1-9][0-9]{0,5}$ ]] || (( managed_heap < 96 || managed_heap > 786432 )); then
        echo '[ERROR] Invalid managed Java heap limit.' >&2
        exit 1
    fi
    java_heap=("-Xmx${managed_heap}M")
fi

echo "=========================================="
echo "  ArcadeNode - Minecraft Server"
echo "=========================================="
echo "[INFO] Server Type: ${SERVER_TYPE}"
echo "[INFO] Minecraft Version: ${MC_VERSION}"

if [ "${ARCADENODE_RESTORED_VOLUME:-false}" = "true" ]; then
    # Pterodactyl stores the JAR, world and configuration at the root of the game volume.
    cd "${MINECRAFT_HOME}"
    if [ ! -f "${server_jar_name}" ] || [ -L "${server_jar_name}" ] ||
        [ ! -f server.properties ] || [ -L server.properties ] ||
        [ ! -f eula.txt ] || [ -L eula.txt ]; then
        echo "[ERROR] Restored volume must contain a regular server JAR, server.properties and eula.txt." >&2
        exit 1
    fi
    read_setting() {
        awk -v key="$1" '
            { sub(/\r$/, "") }
            index($0, key "=") == 1 { count++; value = substr($0, length(key) + 2) }
            END { if (count != 1) exit 1; print value }
        ' "$2"
    }
    matches_setting() {
        [ "$(read_setting "$1" "$3")" = "$2" ]
    }
    if ! matches_setting eula true eula.txt ||
        ! matches_setting server-port "${SERVER_PORT}" server.properties; then
        echo "[ERROR] Restored configuration requires accepted EULA and the port reserved on ArcadeNode." >&2
        exit 1
    fi
    if matches_setting enable-query true server.properties &&
        ! matches_setting query.port "${QUERY_PORT:-${SERVER_PORT}}" server.properties; then
        echo "[ERROR] Restored query port differs from the approved mapping." >&2
        exit 1
    fi
    if matches_setting enable-rcon true server.properties; then
        if ! matches_setting rcon.port "${RCON_PORT}" server.properties; then
            echo "[ERROR] Restored RCON port differs from the approved mapping." >&2
            exit 1
        fi
        restored_rcon_password="$(read_setting rcon.password server.properties)"
        if [ "${#restored_rcon_password}" -lt 16 ] || [ "${restored_rcon_password}" = "changeme" ]; then
            echo "[ERROR] Restored RCON requires a unique password of at least 16 characters." >&2
            exit 1
        fi
        unset restored_rcon_password
    fi
    exec "$java_bin" "${java_heap[@]}" -jar "./${server_jar_name}" --nogui
fi

ENABLE_RCON="${RCON_ENABLED:-${ENABLE_RCON:-false}}"
if [ "${ENABLE_RCON}" = "true" ] && { [ "${#RCON_PASSWORD}" -lt 16 ] || [ "${RCON_PASSWORD}" = "changeme" ]; }; then
    echo "[ERROR] Set a unique RCON_PASSWORD of at least 16 characters" >&2
    exit 1
fi

# Create data directories
mkdir -p "${DATA_DIR}/world"
mkdir -p "${DATA_DIR}/world_nether"
mkdir -p "${DATA_DIR}/world_the_end"
mkdir -p "${DATA_DIR}/logs"
mkdir -p "${PLUGINS_DIR}"
mkdir -p "${SERVER_DIR}"

cd "${SERVER_DIR}"

# Download server JAR based on type
SERVER_JAR="${SERVER_DIR}/${server_jar_name}"
if [ -L "${SERVER_JAR}" ] || { [ -e "${SERVER_JAR}" ] && [ ! -f "${SERVER_JAR}" ]; }; then
    echo '[ERROR] The server JAR path must identify a regular file.' >&2
    exit 1
fi

# Check if we need to download/update the server
if [ ! -f "${SERVER_JAR}" ]; then
    case "${SERVER_TYPE}" in
        paper|spigot)
            if [ "${SERVER_TYPE}" = spigot ]; then
                echo "[WARN] Spigot requires BuildTools. Using Paper instead..."
            fi
            /usr/local/bin/arcadenode-download-paper "${MC_VERSION}" "${PAPER_BUILD:-latest}" "${SERVER_JAR}"
            ;;
        vanilla)
            /usr/local/bin/arcadenode-download-vanilla "${MC_VERSION}" "${SERVER_JAR}"
            ;;
        *)
            echo "[ERROR] Unknown server type: ${SERVER_TYPE}"
            exit 1
            ;;
    esac
fi

if [ "${JAVA_RUNTIME:-auto}" = auto ]; then
    runtime_file="${SERVER_JAR}.java-runtime"
    if [ -f "$runtime_file" ] && [ ! -L "$runtime_file" ]; then
        read -r java_runtime < "$runtime_file"
        select_java "$java_runtime"
    fi
fi

# Accept EULA
echo "[INFO] Accepting EULA..."
echo "eula=true" > "${SERVER_DIR}/eula.txt"

# Generate server.properties
echo "[INFO] Generating server.properties..."
cat > "${SERVER_DIR}/server.properties" << EOF
# Minecraft Server Properties
# Generated by ArcadeNode

# Server
server-port=${SERVER_PORT}
server-name=${SERVER_NAME}
motd=${MOTD}
max-players=${MAX_PLAYERS}
difficulty=${DIFFICULTY}
gamemode=${GAMEMODE}
level-name=${LEVEL_NAME}
level-seed=${LEVEL_SEED}
level-type=${LEVEL_TYPE}
max-world-size=${MAX_WORLD_SIZE}

# Performance
view-distance=${VIEW_DISTANCE}
simulation-distance=${SIMULATION_DISTANCE}
network-compression-threshold=256

# Security
online-mode=${ONLINE_MODE}
white-list=${WHITE_LIST}
enforce-whitelist=${ENFORCE_WHITELIST}
spawn-protection=${SPAWN_PROTECTION}

# Gameplay
pvp=${PVP}
allow-nether=${ALLOW_NETHER}
allow-flight=${ALLOW_FLIGHT}
spawn-animals=${SPAWN_ANIMALS}
spawn-monsters=${SPAWN_MONSTERS}
spawn-npcs=${SPAWN_NPCS}
generate-structures=${GENERATE_STRUCTURES}
enable-command-block=${ENABLE_COMMAND_BLOCK}

# RCON
enable-rcon=${ENABLE_RCON}
rcon.port=${RCON_PORT}
rcon.password=${RCON_PASSWORD}
broadcast-rcon-to-ops=true

# Query
enable-query=${ENABLE_QUERY}
query.port=${QUERY_PORT:-${SERVER_PORT}}

# Misc
force-gamemode=false
hardcore=false
hide-online-players=false
prevent-proxy-connections=false
resource-pack=
resource-pack-sha1=
sync-chunk-writes=true
use-native-transport=true
EOF

# Link world directories
ln -sfn "${DATA_DIR}/world" "${SERVER_DIR}/world" 2>/dev/null || true
ln -sfn "${DATA_DIR}/world_nether" "${SERVER_DIR}/world_nether" 2>/dev/null || true
ln -sfn "${DATA_DIR}/world_the_end" "${SERVER_DIR}/world_the_end" 2>/dev/null || true
ln -sfn "${DATA_DIR}/logs" "${SERVER_DIR}/logs" 2>/dev/null || true
ln -sfn "${PLUGINS_DIR}" "${SERVER_DIR}/plugins" 2>/dev/null || true

# Set up ops if specified
if [ -n "${OPS}" ]; then
    echo "[INFO] Setting up operators..."
    # Convert comma-separated list to JSON array
    echo "[" > "${SERVER_DIR}/ops.json"
    IFS=',' read -ra OP_ARRAY <<< "${OPS}"
    for i in "${!OP_ARRAY[@]}"; do
        OP="${OP_ARRAY[$i]}"
        if [ $i -gt 0 ]; then
            echo "," >> "${SERVER_DIR}/ops.json"
        fi
        cat >> "${SERVER_DIR}/ops.json" << OPENTRY
    {
        "uuid": "",
        "name": "${OP}",
        "level": 4,
        "bypassesPlayerLimit": true
    }
OPENTRY
    done
    echo "]" >> "${SERVER_DIR}/ops.json"
fi

# Upload icons through a future validated asset endpoint; no arbitrary URL fetch.

echo ""
echo "[INFO] Starting Minecraft Server..."
echo "[INFO] Server Type: ${SERVER_TYPE}"
echo "[INFO] Version: ${MC_VERSION}"
echo "[INFO] Max Players: ${MAX_PLAYERS}"
echo "[INFO] Game Port: ${SERVER_PORT}"
echo "[INFO] RCON Port: ${RCON_PORT}"
if [[ -n "$managed_heap" ]]; then
    echo "[INFO] Maximum Java heap: ${managed_heap} MiB"
fi
echo ""

# Start the server
if [ "${SERVER_TYPE}" = vanilla ]; then
    exec "$java_bin" "${java_heap[@]}" -jar "${SERVER_JAR}" --nogui "$@"
fi

paper_heap=("-Xms${MIN_RAM}" "-Xmx${MAX_RAM}")
paper_tuning=(-XX:+AlwaysPreTouch -XX:G1HeapRegionSize=8M)
if [[ -n "$managed_heap" ]]; then
    initial_heap="${MIN_MEMORY:-512}"
    if [[ ! "$initial_heap" =~ ^[1-9][0-9]{0,5}$ ]]; then
        echo '[ERROR] MIN_MEMORY must be a positive number of MiB.' >&2
        exit 1
    fi
    if (( initial_heap > managed_heap )); then initial_heap="$managed_heap"; fi
    paper_heap=("-Xms${initial_heap}M" "${java_heap[@]}")
    paper_tuning=()
else
    echo "[INFO] Java heap: ${MIN_RAM} - ${MAX_RAM}"
fi

exec "$java_bin" \
    "${paper_heap[@]}" \
    "${paper_tuning[@]}" \
    -XX:+UseG1GC \
    -XX:+ParallelRefProcEnabled \
    -XX:MaxGCPauseMillis=200 \
    -XX:+UnlockExperimentalVMOptions \
    -XX:+DisableExplicitGC \
    -XX:G1NewSizePercent=30 \
    -XX:G1MaxNewSizePercent=40 \
    -XX:G1ReservePercent=20 \
    -XX:G1HeapWastePercent=5 \
    -XX:G1MixedGCCountTarget=4 \
    -XX:InitiatingHeapOccupancyPercent=15 \
    -XX:G1MixedGCLiveThresholdPercent=90 \
    -XX:G1RSetUpdatingPauseTimePercent=5 \
    -XX:SurvivorRatio=32 \
    -XX:+PerfDisableSharedMem \
    -XX:MaxTenuringThreshold=1 \
    -Dusing.aikars.flags=https://mcflags.emc.gs \
    -Daikars.new.flags=true \
    -jar "${SERVER_JAR}" \
    --nogui \
    "$@"
