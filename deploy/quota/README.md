# Driver de quota XFS

O driver `arcadenode-quota` roda no host Linux com Docker Engine rootful. Ele aceita volumes com o nome `arcadenode-<uuid sem hífens>-data` e uma opção `size` em bytes. Cada volume recebe um projeto XFS com limite rígido próprio e pertence ao UID/GID 10001 no host. O driver confere a quota no filesystem ao retornar o caminho para o Docker e preserva volumes com dados quando recebe um pedido de remoção.

## Instalação no node

1. Prepare um filesystem XFS dedicado a esses volumes e monte-o em `/srv/arcadenode-quota` com `prjquota`. Mantenha o diretório raiz sob controle de root e reserve os IDs de projeto desse filesystem para o ArcadeNode. Instale `xfsprogs`, com as ferramentas `xfs_quota` e `xfs_io`. No Alpine, instale também `xfsprogs-extra`. [Quotas de projeto no XFS](https://man7.org/linux/man-pages/man8/xfs_quota.8.html)
2. Publique para a arquitetura do node. Em Alpine, use o RID `linux-musl-x64` ou `linux-musl-arm64`.

   ```sh
   dotnet publish backend/ArcadeNode.Quota -c Release -r linux-x64 --self-contained true -o /tmp/arcadenode-quota-release
   ```

3. Instale a saída em `/opt/arcadenode-quota`, sob controle de root. Instale [a unidade systemd](arcadenode-quota.service) em `/etc/systemd/system/` e [a especificação do plugin](arcadenode-quota.spec) em `/etc/docker/plugins/`. O socket Unix permanece em um diretório privado de root. [Descoberta de plugins pelo Docker Engine](https://docs.docker.com/engine/extend/plugin_api/)
4. Habilite o serviço no node e execute o teste em um ambiente descartável. O systemd aguarda a inicialização do socket antes de concluir a partida do serviço.

   ```sh
   sudo systemctl daemon-reload
   sudo systemctl enable --now arcadenode-quota.service
   bash scripts/security-probes/quota-plugin-smoke.sh
   ```

5. Após verificar a quota, configure o node no painel com `QuotaVolumeDriver` igual a `arcadenode-quota`, `QuotaSizeOption` igual a `size` e `QuotaEnforcementVerified` igual a `true`.

Os dados ficam em `/srv/arcadenode-quota/volumes/<nome do volume>`. Monte `volumes` no serviço de arquivos e configure `Files:ManagedVolumesDirectory` para habilitar a descoberta por UUID. O driver publica cada diretório após aplicar a quota XFS e atribuir o UID/GID 10001. Inclua os metadados privados em `/srv/arcadenode-quota/.arcadenode-quota` no backup do filesystem. Preserve os IDs de projeto e as quotas ao restaurar o filesystem. O guia de migração usa o mesmo nome e o mesmo limite ao preparar o volume definitivo. [Configuração do serviço de arquivos](../../docs/ARQUIVOS_NODES_PT-BR.md#configuração-no-node) · [Guia de migração](../../docs/MIGRACAO_PTERODACTYL_PT-BR.md)
