# Arma Reforger Dedicated Server
# ArcadeNode Game Server Cartridge

FROM steamcmd/steamcmd:ubuntu-24

LABEL maintainer="ArcadeNode"
LABEL game="Arma Reforger"
LABEL version="2.0"
LABEL arcadenode.managed="2"
LABEL arcadenode.restore="1"

# Environment variables
ENV STEAM_APP_ID=1874900
ENV SERVER_NAME="ArcadeNode Reforger Server"
ENV GAME_PORT=2001
ENV STEAM_QUERY_PORT=17777
ENV A2S_PORT=17777
ENV MAX_PLAYERS=64
ENV VISIBLE=true
ENV CROSSPLATFORM=true
ENV BATTLYE=true
ENV DISABLE_THIRD_PERSON=false
ENV SCENARIO_ID="{ECC61978EDCC2B5A}Missions/23_Campaign.conf"
ENV GAME_MODE=""
ENV SERVER_MAX_VIEW_DISTANCE=2500
ENV SERVER_MIN_GRASS_DISTANCE=50
ENV NETWORK_VIEW_DISTANCE=1500
ENV MODS=""
ENV HOME=/home/reforger
ENV USER=reforger

# Install dependencies
RUN apt-get update && apt-get install -y \
    lib32gcc-s1 \
    libcurl4 \
    curl \
    jq \
    netcat-openbsd \
    gettext-base \
    && rm -rf /var/lib/apt/lists/*

# Create server user and directories
RUN groupadd -g 10001 reforger \
    && useradd -m -u 10001 -g 10001 -s /bin/bash reforger \
    && mkdir -p /home/reforger/server \
    && mkdir -p /home/reforger/data/profile \
    && mkdir -p /home/reforger/data/addons \
    && mkdir -p /home/reforger/data/configs \
    && chown -R reforger:reforger /home/reforger

# Switch to server user
WORKDIR /home/reforger

# Copy configuration files
COPY entrypoint.sh /usr/local/bin/arcadenode-entrypoint
COPY config.jq /usr/local/share/arcadenode/reforger-config.jq
RUN chmod 0555 /usr/local/bin/arcadenode-entrypoint

# Expose ports
# Game port (UDP)
EXPOSE 2001/udp
# Steam Query port (UDP)
EXPOSE 17777/udp

# Volumes for persistence

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=180s --retries=3 \
    CMD netcat -zu localhost ${GAME_PORT} || exit 1

USER 10001:10001
STOPSIGNAL SIGINT
ENTRYPOINT ["/usr/local/bin/arcadenode-entrypoint"]
