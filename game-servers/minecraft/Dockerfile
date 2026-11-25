# Minecraft Server (Paper/Vanilla)
# ArcadeNode Game Server Cartridge

FROM eclipse-temurin:25-jre-alpine AS java25
FROM eclipse-temurin:21-jre-alpine

LABEL maintainer="ArcadeNode"
LABEL game="Minecraft"
LABEL version="2.0"
LABEL arcadenode.managed="2"
LABEL arcadenode.restore="1"

# Environment variables
ENV SERVER_TYPE=paper
ENV MC_VERSION=1.21.1
ENV JAVA_RUNTIME=auto
ENV PAPER_BUILD=latest
ENV SERVER_NAME="ArcadeNode MC Server"
ENV SERVER_PORT=25565
ENV RCON_PORT=25575
ENV MAX_PLAYERS=20
ENV MOTD="ArcadeNode Minecraft Server"
ENV DIFFICULTY=normal
ENV GAMEMODE=survival
ENV LEVEL_NAME=world
ENV LEVEL_SEED=""
ENV LEVEL_TYPE=minecraft:normal
ENV MAX_WORLD_SIZE=29999984
ENV VIEW_DISTANCE=10
ENV SIMULATION_DISTANCE=10
ENV MAX_RAM=2G
ENV MIN_RAM=1G
ENV ONLINE_MODE=true
ENV WHITE_LIST=false
ENV ENFORCE_WHITELIST=false
ENV PVP=true
ENV ALLOW_NETHER=true
ENV ALLOW_FLIGHT=false
ENV SPAWN_PROTECTION=16
ENV SPAWN_ANIMALS=true
ENV SPAWN_MONSTERS=true
ENV SPAWN_NPCS=true
ENV GENERATE_STRUCTURES=true
ENV ENABLE_RCON=false
ENV ENABLE_QUERY=true
ENV ENABLE_COMMAND_BLOCK=false
ENV OPS=""
ENV ICON_URL=""

# Install dependencies
RUN apk add --no-cache \
    curl \
    jq \
    bash \
    netcat-openbsd \
    wget

# Create server user and directories
RUN addgroup -g 10001 minecraft \
    && adduser -D -u 10001 -G minecraft -h /home/minecraft minecraft \
    && mkdir -p /home/minecraft/server \
    && mkdir -p /home/minecraft/data \
    && mkdir -p /home/minecraft/plugins \
    && chown -R minecraft:minecraft /home/minecraft

# Switch to server user
WORKDIR /home/minecraft

# Copy configuration files
COPY entrypoint.sh /usr/local/bin/arcadenode-entrypoint
COPY download-vanilla.sh /usr/local/bin/arcadenode-download-vanilla
COPY download-paper.sh /usr/local/bin/arcadenode-download-paper
COPY --from=java25 /opt/java/openjdk/ /opt/java/java25/
COPY --chown=minecraft:minecraft server.properties.template /home/minecraft/server.properties.template
RUN chmod 0555 /usr/local/bin/arcadenode-entrypoint /usr/local/bin/arcadenode-download-vanilla /usr/local/bin/arcadenode-download-paper

# Expose ports
# Game port (TCP/UDP)
EXPOSE 25565/tcp
EXPOSE 25565/udp
# RCON port (TCP)
EXPOSE 25575/tcp
# Query port (UDP)
EXPOSE 25565/udp

# Volumes for persistence

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=3 \
    CMD nc -z 127.0.0.1 "${SERVER_PORT}" || exit 1

USER 10001:10001
ENTRYPOINT ["/usr/local/bin/arcadenode-entrypoint"]
