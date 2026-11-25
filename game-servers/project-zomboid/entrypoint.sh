#!/bin/bash
set -e
umask 027

# =============================================================================
# ArcadeNode - Project Zomboid Server Entrypoint
# =============================================================================
# Directory structure:
#   /home/pzserver/server     - PZ server installation (from SteamCMD)
#   /home/pzserver/data       - Persistent data volume (mounted)
#   /home/pzserver/.cache     - Generated configs, scripts, logs
#   /home/pzserver/Zomboid    - PZ runtime directory (links to data)
# =============================================================================

PZ_HOME="/home/pzserver"
SERVER_DIR="${PZ_HOME}/server"
DATA_DIR="${PZ_HOME}/data"
CACHE_DIR="${PZ_HOME}/.cache"
ZOMBOID_DIR="${PZ_HOME}/Zomboid"
STEAMCMD="/usr/bin/steamcmd"
DIRECT_PORT="${STEAM_PORT:-${DIRECT_PORT:-16262}}"
steam_mode=()
case "${NO_STEAM:-false}" in
    0|false) ;;
    1|true) steam_mode=(-nosteam) ;;
    *) echo '[ERROR] NO_STEAM must be 0, 1, false or true.' >&2; exit 1 ;;
esac

configure_managed_heap() (
    local heap="${ARCADENODE_JAVA_HEAP_MIB:-}" config size temporary
    if [[ -z "$heap" ]]; then return 0; fi
    if [[ ! "$heap" =~ ^[1-9][0-9]{0,5}$ ]] || (( heap < 96 || heap > 786432 )); then
        echo '[ERROR] Invalid managed Java heap limit.' >&2
        return 1
    fi

    config="${SERVER_DIR}/ProjectZomboid64.json"
    if [[ -L "$SERVER_DIR" || ! -f "$config" || -L "$config" ]] ||
        ! size="$(stat -c %s -- "$config")" || (( size > 65536 )); then
        echo '[ERROR] A regular ProjectZomboid64.json launcher configuration is required.' >&2
        return 1
    fi

    temporary="$(mktemp -- "${SERVER_DIR}/.arcadenode-jvm.XXXXXX")" || return 1
    trap 'rm -f -- "$temporary"' EXIT
    if ! jq -e -s --arg max "-Xmx${heap}m" '
        if length != 1 or (.[0].vmArgs | type) != "array" or (.[0].vmArgs | any(type != "string"))
        then error("Expected a single launcher configuration with a vmArgs array of strings")
        else .[0] | .vmArgs |= (
            map(select(test("^-Xm[sx]|^-XX:(InitialHeapSize|MinHeapSize|MaxHeapSize|SoftMaxHeapSize)=") | not)) + [$max]
        )
        end
    ' "$config" > "$temporary"; then
        echo '[ERROR] Could not configure the Project Zomboid launcher heap.' >&2
        return 1
    fi
    if ! cmp -s -- "$config" "$temporary"; then
        chmod --reference="$config" -- "$temporary" && mv -fT -- "$temporary" "$config" || return 1
    fi
    echo "[INFO] Maximum Java heap: ${heap} MiB"
)

# Sanitize server name - replace spaces with underscores for internal use
SERVER_NAME_SAFE=$(printf '%s' "${SERVER_NAME}" | tr ' ' '_' | LC_ALL=C tr -cd '[:alnum:]_-')
if [ -z "${SERVER_NAME_SAFE}" ]; then
    SERVER_NAME_SAFE="servertest"
fi

echo "=========================================="
echo "  ArcadeNode - Project Zomboid Server"
echo "=========================================="
echo "[INFO] Server Name: ${SERVER_NAME}"

if [ "${ARCADENODE_RESTORED_VOLUME:-false}" = "true" ]; then
    published_ports=",${ARCADENODE_PUBLISHED_PORTS:-},"
    if [[ "$DIRECT_PORT" == "$SERVER_PORT" || ( "$published_ports" != ',,' && "$published_ports" != *",${DIRECT_PORT},"* ) ]]; then
        echo '[ERROR] Zomboid requires a separate, published direct connection port.' >&2
        exit 1
    fi

    # The Pterodactyl egg uses the native launcher and stores game state under .cache.
    if [[ ! -L "$SERVER_DIR" && -x "${SERVER_DIR}/start-server.sh" && -f "${SERVER_DIR}/start-server.sh" &&
        ! -L "${SERVER_DIR}/start-server.sh" ]]; then
        if [[ -L "$ZOMBOID_DIR" ]]; then
            echo '[ERROR] Invalid restored Zomboid state directory.' >&2
            exit 1
        fi
        for component in Saves Server Logs db; do
            active="${ZOMBOID_DIR}/${component}"
            if [[ -L "$active" ]] &&
                [[ "$(readlink -- "$active")" != "../data/$component" || -L "$DATA_DIR" ||
                    -L "$DATA_DIR/$component" || ! -d "$DATA_DIR/$component" ]]; then
                echo '[ERROR] Invalid restored Zomboid state link.' >&2
                exit 1
            fi
        done
        launcher=./start-server.sh
    elif [[ -x "${PZ_HOME}/ProjectZomboid64" && -f "${PZ_HOME}/ProjectZomboid64" && ! -L "${PZ_HOME}/ProjectZomboid64" &&
        -x "${PZ_HOME}/jre64/bin/java" ]]; then
        case "$SERVER_NAME" in
            ''|'.'|'..'|*/*|*\\*|*[[:cntrl:]]*) echo '[ERROR] Invalid restored Zomboid save name.' >&2; exit 1 ;;
        esac
        SERVER_NAME_SAFE="$SERVER_NAME"
        SERVER_DIR="$PZ_HOME"
        ZOMBOID_DIR="$CACHE_DIR"
        launcher=./ProjectZomboid64
    else
        echo '[ERROR] The restored volume does not contain a supported Project Zomboid installation.' >&2
        exit 1
    fi
    restored_config="${ZOMBOID_DIR}/Server/${SERVER_NAME_SAFE}.ini"
    restored_save="${ZOMBOID_DIR}/Saves/Multiplayer/${SERVER_NAME_SAFE}"
    restored_db="${ZOMBOID_DIR}/db/${SERVER_NAME_SAFE}.db"
    if [ ! -f "${restored_config}" ] || [ -L "${restored_config}" ] ||
        [ ! -d "${restored_save}" ] || [ -L "${restored_save}" ] ||
        [ ! -f "${restored_db}" ] || [ -L "${restored_db}" ]; then
        echo "[ERROR] Restore requires the installed build, existing configuration, save and player database for the selected server name." >&2
        exit 1
    fi

    if ! awk -v game="${SERVER_PORT}" -v direct="${DIRECT_PORT}" -v rcon="${RCON_PORT}" -v published="${published_ports}" '
        { sub(/\r$/, "") }
        index($0, "DefaultPort=") == 1 { main_count++; actual_main = substr($0, 13) }
        index($0, "UDPPort=") == 1 { direct_count++; actual_direct = substr($0, 9) }
        index($0, "RCONPort=") == 1 { rcon_count++; actual_rcon = substr($0, 10) }
        index($0, "RCONPassword=") == 1 { password_count++; password = substr($0, 14) }
        END {
            rcon_mismatch = actual_rcon+0 != rcon+0 && (published == ",," ||
                index(published, "," (actual_rcon+0) ",") || index(published, "," (rcon+0) ","))
            exit(main_count != 1 || direct_count != 1 || actual_main !~ /^[0-9]+$/ || actual_direct !~ /^[0-9]+$/ ||
                actual_main+0 != game+0 || actual_direct+0 != direct+0 ||
                rcon_count > 1 || password_count > 1 || (length(password) &&
                    (rcon_count != 1 || actual_rcon !~ /^[0-9]+$/ || actual_rcon+0 < 1024 || actual_rcon+0 > 65535 ||
                        rcon_mismatch || length(password) < 16 || password == "changeme")))
        }
    ' "${restored_config}"; then
        echo "[ERROR] Restored Zomboid ports or RCON credentials need review." >&2
        exit 1
    fi
    export HOME="${PZ_HOME}"
    export USER="pzserver"
    cd "${SERVER_DIR}"
    configure_managed_heap
    if [[ "$launcher" == ./ProjectZomboid64 ]]; then
        export PATH="${SERVER_DIR}/jre64/bin:$PATH"
        export LD_LIBRARY_PATH="${SERVER_DIR}/linux64:${SERVER_DIR}/natives:${SERVER_DIR}:${SERVER_DIR}/jre64/lib/amd64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
        for signal_library in jre64/lib/libjsig.so jre64/lib/amd64/libjsig.so; do
            if [[ -f "$signal_library" ]]; then
                export LD_PRELOAD="${LD_PRELOAD:+$LD_PRELOAD:}${SERVER_DIR}/${signal_library}"
                break
            fi
        done
    fi
    exec "$launcher" -servername "${SERVER_NAME_SAFE}" -ip 0.0.0.0 \
        -port "${SERVER_PORT}" -udpport "${DIRECT_PORT}" -cachedir="${ZOMBOID_DIR}" "${steam_mode[@]}"
fi

if [ "${#ADMIN_PASSWORD}" -lt 16 ] || [ "${ADMIN_PASSWORD}" = "changeme" ]; then
    echo "[ERROR] ADMIN_PASSWORD must be a unique secret of at least 16 characters" >&2
    exit 1
fi
if [[ "$DIRECT_PORT" == "$SERVER_PORT" ]]; then
    echo '[ERROR] Zomboid requires a separate direct connection port.' >&2
    exit 1
fi

case "${AUTO_UPDATE:-0}" in
    0|false) auto_update=false ;;
    1|true) auto_update=true ;;
    *) echo '[ERROR] AUTO_UPDATE must be 0, 1, false or true.' >&2; exit 1 ;;
esac

if [[ "${STEAM_APP_ID:-380870}" != 380870 ]]; then
    echo '[ERROR] The Project Zomboid cartridge requires Steam app 380870.' >&2
    exit 1
fi
if [[ -n "$BETA_BRANCH" && ! "$BETA_BRANCH" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$ ]]; then
    echo '[ERROR] Invalid Steam branch name.' >&2
    exit 1
fi
if [[ -n "$BETA_PASSWORD" && ( -z "$BETA_BRANCH" || "$BETA_PASSWORD" == [+-]* || "$BETA_PASSWORD" =~ [[:space:][:cntrl:]] ||
    "$BETA_PASSWORD" == *\"* || "$BETA_PASSWORD" == *\\* || "$BETA_PASSWORD" == *\;* ) ]]; then
    echo '[ERROR] Invalid Steam beta password argument.' >&2
    exit 1
fi

if [[ -L "$SERVER_DIR" || -L "$DATA_DIR" || -L "$CACHE_DIR" || -L "$ZOMBOID_DIR" ]]; then
    echo '[ERROR] Game installation and data directories must be inside the game volume.' >&2
    exit 1
fi

# =============================================================================
# Setup .cache directory structure for generated files
# =============================================================================
echo "[INFO] Setting up cache directories..."
mkdir -p "${CACHE_DIR}/configs"      # Generated config files
mkdir -p "${CACHE_DIR}/logs"         # Startup and operation logs
mkdir -p "${CACHE_DIR}/templates"    # Processed templates

# Log startup
echo "$(date -Iseconds) - Container starting: ${SERVER_NAME_SAFE}" >> "${CACHE_DIR}/logs/startup.log"

# =============================================================================
# Preserve existing Zomboid state and create missing directories
# =============================================================================
echo "[INFO] Setting up data directories..."

mkdir -p "${DATA_DIR}" "${ZOMBOID_DIR}"
for component in Saves Server Logs db; do
    stored="${DATA_DIR}/${component}"
    active="${ZOMBOID_DIR}/${component}"
    if [[ -L "$stored" || ( -e "$stored" && ! -d "$stored" ) ]]; then
        echo "[ERROR] Invalid stored ${component} directory." >&2
        exit 1
    fi
    stored_entry=''
    if [[ -d "$stored" ]]; then
        stored_entry="$(find "$stored" -mindepth 1 -maxdepth 1 -print -quit)"
    fi
    if [[ -L "$active" ]]; then
        linked="$(readlink -- "$active")"
        if [[ "$linked" != "$stored" && "$linked" != "../data/${component}" ]]; then
            echo "[ERROR] Unexpected ${component} link in game state." >&2
            exit 1
        fi
        mkdir -p "$stored"
    elif [[ -d "$active" ]]; then
        if [[ -n "$stored_entry" ]]; then
            echo "[ERROR] Existing ${component} state in both layouts. Choose the authoritative directory before starting." >&2
            exit 1
        fi
    elif [[ -e "$active" ]]; then
        echo "[ERROR] Invalid active ${component} directory." >&2
        exit 1
    elif [[ -n "$stored_entry" ]]; then
        ln -s -- "../data/${component}" "$active"
    else
        mkdir "$active"
    fi
done

# =============================================================================
# Install/Update Project Zomboid via SteamCMD
# =============================================================================
installed_build() {
    [[ -f "${SERVER_DIR}/start-server.sh" && -x "${SERVER_DIR}/start-server.sh" && ! -L "${SERVER_DIR}/start-server.sh" ]]
}

install_pending="${CACHE_DIR}/steam-install.pending"
if [[ -L "$install_pending" || ( -e "$install_pending" && ! -f "$install_pending" ) ]]; then
    echo '[ERROR] Invalid Steam installation state.' >&2
    exit 1
fi
if [[ "$auto_update" == true || -f "$install_pending" ]] || ! installed_build; then
    mkdir -p "${SERVER_DIR}"
    : > "$install_pending"
    steam_args=('+@ShutdownOnFailedCommand' 1 '+@sSteamCmdForcePlatformType' linux
        +force_install_dir "$SERVER_DIR" +login anonymous +app_info_update 1 +app_update 380870)
    if [[ -n "$BETA_BRANCH" ]]; then steam_args+=(-beta "$BETA_BRANCH"); fi
    if [[ -n "$BETA_PASSWORD" ]]; then steam_args+=(-betapassword "$BETA_PASSWORD"); fi
    echo "[INFO] Installing or updating Steam branch: ${BETA_BRANCH:-public}"

    if ! (
        if [[ -n "$BETA_PASSWORD" ]]; then
            "$STEAMCMD" "${steam_args[@]}" validate +quit >/dev/null 2>&1
        else
            set -o pipefail
            "$STEAMCMD" "${steam_args[@]}" validate +quit 2>&1 | tee -a "${CACHE_DIR}/logs/steamcmd.log"
        fi
    ); then
        echo '[ERROR] SteamCMD failed. The next start will retry the same branch.' >&2
        exit 1
    fi
    if ! installed_build; then
        echo '[ERROR] SteamCMD did not install an executable start-server.sh.' >&2
        exit 1
    fi
    rm -f -- "$install_pending"
else
    echo '[INFO] Starting the installed build. Automatic Steam updates are disabled.'
fi

# =============================================================================
# Generate server configuration when the current instance does not have one.
# =============================================================================
CONFIG_FILE="${ZOMBOID_DIR}/Server/${SERVER_NAME_SAFE}.ini"
CACHE_CONFIG="${CACHE_DIR}/configs/${SERVER_NAME_SAFE}.ini"
if [[ -L "$CONFIG_FILE" || ( -e "$CONFIG_FILE" && ! -f "$CONFIG_FILE" ) ]]; then
    echo '[ERROR] Expected a regular server configuration file.' >&2
    exit 1
fi

echo "[INFO] Generating server configuration..."

# Set default values for all environment variables to prevent envsubst issues
export PUBLIC_SERVER="${PUBLIC_SERVER:-true}"
export SERVER_NAME="${SERVER_NAME:-ArcadeNode_PZ_Server}"
export MAX_PLAYERS="${MAX_PLAYERS:-16}"
export SERVER_PASSWORD="${SERVER_PASSWORD:-}"
export PAUSE_EMPTY="${PAUSE_EMPTY:-true}"
export SERVER_PORT="${SERVER_PORT:-16261}"
export DIRECT_PORT="${DIRECT_PORT:-16262}"
export RCON_PORT="${RCON_PORT:-27015}"
export ADMIN_PASSWORD="${ADMIN_PASSWORD:?Set a unique strong ADMIN_PASSWORD}"
export STEAM_VAC="${STEAM_VAC:-true}"
export MOD_IDS="${MOD_IDS:-}"
export WORKSHOP_IDS="${WORKSHOP_IDS:-}"
export MAX_RAM="${MAX_RAM:-4096}"

# Always regenerate config template in cache
if [ -f "/home/pzserver/server.ini.template" ]; then
    envsubst < /home/pzserver/server.ini.template > "${CACHE_CONFIG}"
else
    cat > "${CACHE_CONFIG}" << EOF
# Project Zomboid Server Configuration
# Generated by ArcadeNode at $(date -Iseconds)

Public=${PUBLIC_SERVER}
PublicName=${SERVER_NAME}
PublicDescription=Managed by ArcadeNode Panel
MaxPlayers=${MAX_PLAYERS}
Password=${SERVER_PASSWORD}
PauseEmpty=${PAUSE_EMPTY}
DefaultPort=${SERVER_PORT}
UDPPort=${DIRECT_PORT}
RCONPort=${RCON_PORT}
RCONPassword=${ADMIN_PASSWORD}
SteamVAC=${STEAM_VAC}
Mods=${MOD_IDS}
WorkshopItems=${WORKSHOP_IDS}
Map=Muldraugh, KY
ResetID=0
Open=true
ServerWelcomeMessage=Welcome to ${SERVER_NAME}! <LINE> Managed by ArcadeNode.
GlobalChat=true
AutoCreateUserInWhiteList=false
DisplayUserName=true
ShowFirstAndLastName=false
SpawnPoint=0,0,0
SafetySystem=true
ShowSafety=true
SafetyToggleTimer=2
SafetyCooldownTimer=3
DoLuaChecksum=true
DenyLoginOnOverloadedServer=true
PVP=true
SafeHouse=true
BackupsCount=5
BackupsOnStart=true
BackupsOnVersionChange=true
BackupsPeriod=0
SteamScoreboard=true
SteamPort1=8766
SteamPort2=8767
VoiceEnable=true
EOF
fi

# Copy config to data if doesn't exist or REGENERATE_CONFIG is set
if [ ! -f "${CONFIG_FILE}" ] || [ "${REGENERATE_CONFIG}" = "true" ]; then
    cp "${CACHE_CONFIG}" "${CONFIG_FILE}"
    echo "[INFO] Configuration written to ${CONFIG_FILE}"
else
    echo "[INFO] Using existing config: ${CONFIG_FILE}"
fi

# =============================================================================
# Generate required Lua configuration files
# =============================================================================
echo "[INFO] Generating Lua configuration files..."

# Spawn points file
SPAWN_FILE="${ZOMBOID_DIR}/Server/${SERVER_NAME_SAFE}_spawnpoints.lua"
if [ ! -f "${SPAWN_FILE}" ]; then
    cat > "${CACHE_DIR}/templates/spawnpoints.lua" << 'EOFSPAWN'
function SpawnPoints()
end
EOFSPAWN
    cp "${CACHE_DIR}/templates/spawnpoints.lua" "${SPAWN_FILE}"
    echo "[INFO] Created spawn points file"
fi

# Spawn regions file
REGION_FILE="${ZOMBOID_DIR}/Server/${SERVER_NAME_SAFE}_spawnregions.lua"
if [ ! -f "${REGION_FILE}" ]; then
    cat > "${CACHE_DIR}/templates/spawnregions.lua" << 'EOFREG'
function SpawnRegions()
    return {}
end
EOFREG
    cp "${CACHE_DIR}/templates/spawnregions.lua" "${REGION_FILE}"
    echo "[INFO] Created spawn regions file"
fi

# =============================================================================
# Generate sandbox variables file
# =============================================================================
SANDBOX_FILE="${ZOMBOID_DIR}/Server/${SERVER_NAME_SAFE}_SandboxVars.lua"
if [ ! -f "${SANDBOX_FILE}" ]; then
    echo "[INFO] Creating sandbox variables file..."
    cat > "${CACHE_DIR}/templates/SandboxVars.lua" << 'EOFBOX'
SandboxVars = {
    Version = 5,
    Zombies = 4,
    Distribution = 1,
    DayLength = 2,
    StartYear = 1,
    StartMonth = 7,
    StartDay = 1,
    StartTime = 2,
    WaterShut = 14,
    ElecShut = 14,
    WaterShutModifier = 14,
    ElecShutModifier = 14,
    FoodLoot = 2,
    CannedFoodLoot = 2,
    LiteratureLoot = 2,
    SurvivalGearsLoot = 2,
    MedicalLoot = 2,
    WeaponLoot = 2,
    RangedWeaponLoot = 2,
    AmmoLoot = 2,
    MechanicsLoot = 2,
    OtherLoot = 2,
    Temperature = 3,
    Rain = 3,
    ErosionSpeed = 3,
    XpMultiplier = 1.0,
    Farming = 3,
    CompostTime = 2,
    StatsDecrease = 3,
    NatureAbundance = 3,
    Alarm = 6,
    LockedHouses = 6,
    FoodRotSpeed = 3,
    FridgeFactor = 3,
    LootRespawn = 1,
    TimeSinceApo = 1,
    PlantResilience = 3,
    PlantAbundance = 3,
    EndRegen = 3,
    Helicopter = 3,
    MetaEvent = 2,
    SleepingEvent = 2,
    GeneratorSpawning = 3,
    GeneratorFuelConsumption = 3,
    SurvivorHouseChance = 3,
    VehicleEasyUse = false,
    Zombies = 4,
    ZombieSpeed = 3,
    ZombieStrength = 3,
    ZombieToughness = 3,
    ZombieTransmission = 1,
    ZombieInfectionMortality = 5,
    ZombieCognition = 3,
    ZombieMemory = 3,
    ZombieDecomposition = 3,
    ZombieSight = 3,
    ZombieHearing = 3,
    ZombieSmell = 3,
}
EOFBOX
    cp "${CACHE_DIR}/templates/SandboxVars.lua" "${SANDBOX_FILE}"
fi

# =============================================================================
# Create world save directory
# =============================================================================
WORLD_DIR="${ZOMBOID_DIR}/Saves/Multiplayer/${SERVER_NAME_SAFE}"
if [ ! -d "${WORLD_DIR}" ]; then
    echo "[INFO] Creating world save directory..."
    mkdir -p "${WORLD_DIR}"
fi

# =============================================================================
# Display startup summary
# =============================================================================
echo ""
echo "=========================================="
echo "  Server Configuration Summary"
echo "=========================================="
echo "[INFO] Server Name: ${SERVER_NAME_SAFE}"
echo "[INFO] Max Players: ${MAX_PLAYERS}"
echo "[INFO] Game Port: ${SERVER_PORT}/udp"
echo "[INFO] Direct Port: ${DIRECT_PORT}/udp"
echo "[INFO] RCON Port: ${RCON_PORT}/tcp"
echo "[INFO] Config: ${CONFIG_FILE}"
echo "[INFO] Cache: ${CACHE_DIR}"
echo "[INFO] Data: ${DATA_DIR}"
echo "=========================================="
echo ""

# Log to cache
echo "$(date -Iseconds) - Server starting with config: ${CONFIG_FILE}" >> "${CACHE_DIR}/logs/startup.log"

# The native launcher reads heap options from ProjectZomboid64.json.
configure_managed_heap
if [[ -z "${ARCADENODE_JAVA_HEAP_MIB:-}" ]]; then
    export JAVA_OPTS="-Xms${MAX_RAM}m -Xmx${MAX_RAM}m"
fi

echo "[INFO] Starting Project Zomboid Server..."
export HOME="${PZ_HOME}" USER=pzserver PZ_CACHE_DIR="${CACHE_DIR}"
cd "${SERVER_DIR}"
exec ./start-server.sh -servername "${SERVER_NAME_SAFE}" -adminpassword "${ADMIN_PASSWORD}" -ip 0.0.0.0 \
    -port "${SERVER_PORT}" -udpport "${DIRECT_PORT}" -cachedir="${ZOMBOID_DIR}" "${steam_mode[@]}"
