#!/bin/bash
set -e
umask 027
REFORGER_HOME="/home/reforger"
SERVER_DIR="${REFORGER_HOME}/server"
DATA_DIR="${REFORGER_HOME}/data"
STEAMCMD="/usr/games/steamcmd"

echo "=========================================="
echo "  ArcadeNode - Arma Reforger Server"
echo "=========================================="

if [ "${ARCADENODE_RESTORED_VOLUME:-false}" = "true" ]; then
    restored_config="${DATA_DIR}/configs/server.json"
    restored_profile="${DATA_DIR}/profile"
    restored_addons="${DATA_DIR}/addons"
    startup_args=(-addonsDir "${restored_addons}" -logLevel normal -logStats 60000)

    if [ -x "${REFORGER_HOME}/ArmaReforgerServer" ] || [ -L "${REFORGER_HOME}/ArmaReforgerServer" ]; then
        SERVER_DIR="${REFORGER_HOME}"
        restored_config="${REFORGER_HOME}/config.json"
        restored_profile="${REFORGER_HOME}/profile"
        restored_addons="${REFORGER_HOME}/addons"
        game_project="${restored_addons}/data/ArmaReforger.gproj"
        if [ -L "${restored_addons}/data" ] || [ ! -f "${game_project}" ] || [ -L "${game_project}" ]; then
            echo "[ERROR] Restore requires the installed Pterodactyl game project." >&2
            exit 1
        fi
        if [ -L "${REFORGER_HOME}/tmp" ]; then
            echo "[ERROR] Restore requires a regular temporary addon directory." >&2
            exit 1
        fi
        startup_args=(-gproj "${game_project}" -backendlog -nothrow -listScenarios
            -addonTempDir "${REFORGER_HOME}/tmp")
    fi

    if [ ! -f "${SERVER_DIR}/ArmaReforgerServer" ] || [ ! -x "${SERVER_DIR}/ArmaReforgerServer" ] ||
        [ -L "${SERVER_DIR}/ArmaReforgerServer" ] || [ ! -f "${restored_config}" ] || [ -L "${restored_config}" ] ||
        [ ! -d "${restored_profile}" ] || [ -L "${restored_profile}" ] ||
        [ ! -d "${restored_addons}" ] || [ -L "${restored_addons}" ]; then
        echo "[ERROR] Restore requires the existing executable, configuration, profile and addons." >&2
        exit 1
    fi
    if ! jq -e --argjson primary "${SERVER_PORT}" --arg published "${ARCADENODE_PUBLISHED_PORTS}" '
        def strong: type == "string" and length >= 16;
        def valid_bind: . == "" or . == "0.0.0.0";
        ($published | split(",") | map(tonumber)) as $allowed |
        (.bindPort // .gameHostBindPort // .publicPort // .gameHostRegisterPort // 2001) as $bind |
        (.publicPort // .gameHostRegisterPort // $bind) as $public |
        (.game.passwordAdmin // .adminPassword // "") as $admin |
        (.a2s.port // (if .a2sQueryEnabled == true then (.steamQueryPort // 17777) else null end)) as $query |
        (.rcon.port // (if .rcon != null then 19999 else null end)) as $rcon |
        ($bind == $primary and $public == $primary) and
        ((.bindAddress // .gameHostBindAddress // "") | valid_bind) and
        ($admin == "" or ($admin | strong)) and
        ($query == null or ($query != $primary and ($allowed | index($query) != null) and ((.a2s.address // "") | valid_bind))) and
        ($rcon == null or ($rcon != $primary and $rcon != $query and ($allowed | index($rcon) != null) and
            ((.rcon.address // "") | valid_bind) and (.rcon.password | strong)))
    ' "${restored_config}" >/dev/null 2>&1; then
        echo "[ERROR] Restored Reforger listener or credential configuration needs review." >&2
        exit 1
    fi
    if [ "${SERVER_DIR}" = "${REFORGER_HOME}" ]; then
        mkdir -p "${REFORGER_HOME}/tmp"
    fi
    cd "${SERVER_DIR}"
    exec ./ArmaReforgerServer -config "${restored_config}" -profile "${restored_profile}" "${startup_args[@]}"
fi

ADMIN_PASSWORD="${ADMIN_PASSWORD:-}"
if [ "${#ADMIN_PASSWORD}" -lt 16 ] || [[ "${ADMIN_PASSWORD}" == *[[:space:]]* ]]; then
    echo "[ERROR] ADMIN_PASSWORD must have at least 16 characters and no whitespace." >&2
    exit 1
fi
export ADMIN_PASSWORD
export GAME_PORT="${SERVER_PORT:-${GAME_PORT}}"

mkdir -p "${DATA_DIR}/profile" "${DATA_DIR}/addons" "${DATA_DIR}/configs" "${DATA_DIR}/logs"

CONFIG_FILE="${DATA_DIR}/configs/server.json"
temporary_config="$(mktemp "${DATA_DIR}/configs/.server.json.XXXXXX")"
trap 'rm -f -- "$temporary_config"' EXIT
jq -en -f /usr/local/share/arcadenode/reforger-config.jq > "$temporary_config"
mv -fT -- "$temporary_config" "$CONFIG_FILE"
trap - EXIT

echo "[INFO] Checking for server updates..."
"${STEAMCMD}" +force_install_dir "${SERVER_DIR}" \
    +login anonymous \
    +app_update "${STEAM_APP_ID}" validate \
    +quit

echo "[INFO] Starting Arma Reforger Server..."
echo "[INFO] Server Name: ${SERVER_NAME}"
echo "[INFO] Max Players: ${MAX_PLAYERS}"
echo "[INFO] Game Port: ${GAME_PORT}/udp"
echo "[INFO] Query Port: ${A2S_PORT}/udp"
echo "[INFO] BattlEye: ${BATTLYE}"
echo "[INFO] Cross Platform: ${CROSSPLATFORM}"

cd "${SERVER_DIR}"

# Start the server
exec ./ArmaReforgerServer \
    -config "${CONFIG_FILE}" \
    -profile "${DATA_DIR}/profile" \
    -addonsDir "${DATA_DIR}/addons" \
    -logLevel normal \
    -logStats 60000 \
    "$@"
