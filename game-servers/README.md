# ArcadeNode game images

Images use UID/GID 10001, an immutable entrypoint, no implicit Docker VOLUME and no default administration passwords. All writable game-home data must be placed on a quota-controlled volume by the panel. The root filesystem is read-only.

| Game | Image alias | Quota-controlled home |
| --- | --- | --- |
| Minecraft Java | arcadenode/minecraft:secure | /home/minecraft |
| Minecraft Bedrock | arcadenode/minecraft-bedrock:secure | /home/bedrock |
| Project Zomboid | arcadenode/project-zomboid:secure | /home/pzserver |
| Arma Reforger | arcadenode/arma-reforger:secure | /home/reforger |

New and restored Minecraft Java and Project Zomboid containers provisioned by the panel set the JVM heap ceiling to 75% of the selected Docker memory limit. Paper and Spigot cap their initial heap at this ceiling. For example, a 512 MiB Minecraft reservation allows a 384 MiB heap and leaves 128 MiB for JVM and container overhead. Game versions, mods and plugins may need larger reservations. Project Zomboid applies this setting to the native launcher's `ProjectZomboid64.json`.

From the repository root:

```bash
docker compose -f game-servers/docker-compose.yml --profile image-build build
```

Publish with the operator's registry tooling, scan the built images and enroll immutable digests in the panel configuration. Preload approved images on each node; aliases are mapped exactly to approved digests. The API does not pull arbitrary images. Build helpers may also be used with a deliberate version tag.

The Compose definitions are now build-only: accidental `up` exits without starting a game or exposing ports. Standalone launch wizards have been retired because they bypassed admission, quotas and the panel's isolation policy.

See [deployment and node acceptance](../deploy/README.md) and [the security correction report](../docs/SECURITY_HARDENING_PT-BR.md). No image or live game was built/run during the local audit; SteamCMD, game console compatibility, secondary ports, root filesystem constraints and physical disk quotas require Linux homologation before hosting clients.
