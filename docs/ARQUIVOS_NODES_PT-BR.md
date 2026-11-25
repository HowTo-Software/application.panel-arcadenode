# Download de arquivos nos nodes Linux

O serviço `ArcadeNode.Node` disponibiliza arquivos dos volumes de jogos registrados por UUID. O painel encaminha a requisição ao node do servidor depois de conferir a sessão, as permissões no servidor e seu estado de suspensão. A aba **Arquivos** permite navegar por páginas de até 200 entradas, buscar por parte do nome na pasta atual e baixar arquivos de até 512 MiB pelo caminho relativo. A listagem omite links simbólicos. Atualize o serviço de arquivos no node para habilitar a busca e a paginação.

Se a pasta mudar durante a navegação, **Atualizar** reinicia a listagem na primeira página. A busca percorre o diretório em blocos. Uma página sem resultado ainda pode ter uma próxima página.

Monte somente os volumes atribuídos ao node, em modo somente leitura. O UID do serviço precisa de permissão de leitura nesses volumes. Cada servidor precisa de um volume dedicado, sem aliases, sobreposição ou conteúdo compartilhado entre servidores. A porta do serviço deve ficar acessível à rede privada do painel.

## Configuração no node

Use o Dockerfile em `backend/ArcadeNode.Node` com esse diretório como contexto de build. Monte o JSON como `/app/appsettings.Production.json` e os certificados e volumes nos caminhos declarados. O kernel precisa oferecer `openat2` e acesso a `/proc/self/fd`.

```json
{
  "Files": {
    "Port": 8443,
    "CertificatePath": "/run/secrets/files-node.crt",
    "KeyPath": "/run/secrets/files-node.key",
    "PanelCertificateSha256": ["<SHA-256 hexadecimal do certificado do painel>"],
    "Volumes": {
      "96bcdf0c-a92c-4de3-a626-7bc7e5ba3880": "/game-data/96bcdf0c-a92c-4de3-a626-7bc7e5ba3880"
    }
  }
}
```

Para usar os volumes do driver ArcadeNode, monte apenas `/srv/arcadenode-quota/volumes` do node Docker em `/game-data/managed` no container do serviço de arquivos. Execute o serviço com `--user 10001:10001` e defina `"ManagedVolumesDirectory": "/game-data/managed"` dentro de `Files`. O serviço identifica cada volume pelo UUID à medida que o driver conclui a preparação. Pode iniciar com o diretório gerenciado vazio e aceita também os mapeamentos em `Volumes`, quando apontam para diretórios separados.

O driver configura o diretório gerenciado para que somente root possa criar, mover ou remover os volumes na raiz. Cada volume pertence ao UID 10001. O serviço abre somente o filho direto correspondente ao UUID e rejeita links simbólicos e submontagens. Monte o diretório com `readonly` para leitura e backup. Para habilitar alterações pela aba **Arquivos**, use a montagem com escrita e `Files:AllowUploads`. Dê ao UID 10001 acesso de leitura aos certificados e acesso de escrita às áreas separadas para backups e restaurações. [Driver de quota XFS](../deploy/quota/README.md)

No node Docker, salve o JSON em `/etc/arcadenode/files.json` e os arquivos `files-node.crt` e `files-node.key` em `/etc/arcadenode/files-tls`. Defina `FILES_BIND_IP` com o IP privado pelo qual o painel acessa o node. O exemplo disponibiliza listagem, downloads e medição do disco. Para backups, monte um diretório persistente em `Files:BackupDirectory` conforme a seção de backups.

```sh
docker build -t arcadenode-files ./backend/ArcadeNode.Node
docker run -d --name arcadenode-files --restart unless-stopped \
  --user 10001:10001 --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:size=16m,mode=1777 \
  --mount type=bind,src=/etc/arcadenode/files.json,dst=/app/appsettings.Production.json,readonly \
  --mount type=bind,src=/etc/arcadenode/files-tls,dst=/run/secrets,readonly \
  --mount type=bind,src=/srv/arcadenode-quota/volumes,dst=/game-data/managed,readonly \
  --publish "${FILES_BIND_IP:?Use o IP privado do node}:8443:8443" arcadenode-files
```

## Configuração no painel

Adicione `FileNodes` ao JSON privado carregado por `NodeConfigurationPath`. Use o UUID do node como chave. O certificado apresentado pelo node precisa corresponder ao nome do host e ao SHA-256 configurado. O certificado do painel precisa estar autorizado no node.

```json
{
  "FileNodes": {
    "bde71054-a12e-42ce-8551-ce1dc6646d3f": {
      "Endpoint": "https://files-node.example.internal:8443",
      "ServerCertificateSha256": "<SHA-256 hexadecimal do certificado do node>",
      "ClientCertificatePath": "/run/secrets/panel-files.crt",
      "ClientKeyPath": "/run/secrets/panel-files.key"
    }
  }
}
```

Use os fingerprints SHA-256 do certificado inteiro, em hexadecimal contínuo. Após uma troca de certificado do node, atualize o fingerprint no painel e reinicie o serviço do painel. O node aceita mais de um fingerprint do painel durante a rotação.

## Download de arquivos

Aplique a migration `FileDownloadGrants`, atualize os nodes para a rota `/v1/servers/{id}/files/stream` e mantenha a interface e a API na mesma origem. A permissão `file.read-content` autoriza o download. O painel confere o caminho e o tamanho no node e emite uma autorização para aquele arquivo, válida por um minuto e de uso único. Para usar a autorização, a conta e a sessão de origem precisam continuar ativas e as permissões concedidas precisam continuar válidas.

O navegador recebe o arquivo como anexo. O painel e o node transmitem em blocos de 64 KiB, com prazo de dez minutos por transferência. O proxy encaminha a resposta sem armazenar o arquivo. O editor e clientes existentes ainda usam `GET /api/servers/{id}/files/download`, com o limite de 8 MiB. [Configuração de proxy do Nginx](https://nginx.org/en/docs/http/ngx_http_proxy_module.html#proxy_buffering)

## Upload de arquivos novos

O upload aceita até 512 MiB por arquivo, respeita o limite de disco do servidor e fica desativado por padrão. Para habilitar, defina `Files:AllowUploads` como `true`, monte os volumes atribuídos com escrita e execute o serviço com o UID/GID do processo do jogo para que ambos acessem os arquivos criados. Use um sistema de arquivos que suporte `O_TMPFILE`. [Manual do Linux](https://man7.org/linux/man-pages/man2/open.2.html)

O navegador mostra o progresso e permite cancelar. A API e o node transmitem os dados em blocos de 64 KiB usando o comprimento declarado na requisição. O node permite até dois envios ativos e tem um prazo de cinco minutos por envio. Ele publica o arquivo completo com `linkat`, recusa um nome existente e descarta a escrita incompleta em caso de interrupção. O proxy aceita o corpo maior e desativa o buffer de requisição somente na rota de upload. [Configuração de proxy do Nginx](https://nginx.org/en/docs/http/ngx_http_proxy_module.html#proxy_request_buffering)

Com gravação habilitada, a aba **Arquivos** permite abrir várias configurações UTF-8 de até 8 KiB enquanto o container está parado. Cada arquivo mantém seu rascunho ao alternar entre arquivos e abas do mesmo servidor. Salve cada arquivo antes de sair da página do servidor. Ao fechar um arquivo com alterações pendentes, o painel pede confirmação. O node compara o SHA-256 da última versão lida ou salva antes de publicar cada alteração. Mantenha operações externas de escrita suspensas durante a edição.

Com o mesmo `Files:AllowUploads`, **Nova pasta** cria um diretório dentro da pasta aberta. **Renomear ou mover** recebe o caminho de destino a partir da raiz do jogo e aceita arquivos regulares e pastas. O painel confirma pelo Docker que o container está parado. O node limita origem e destino ao volume atribuído e usa a operação atômica do Linux que recusa um destino já existente. [Manual de renameat2](https://man7.org/linux/man-pages/man2/rename.2.html)

**Permissões** mostra o modo atual e permite definir os bits de leitura, gravação e execução de um item ou de até 40 itens selecionados na mesma pasta. Use três dígitos octais, como `640` ou `750`, com zero inicial opcional. A operação exige `file.update`, o container parado e gravação habilitada no node. Ela altera somente os itens escolhidos. O node recusa links, arquivos especiais e bits especiais, como setuid, e valida todos os itens antes da primeira mudança. [Manual do chmod](https://man7.org/linux/man-pages/man2/chmod.2.html)

**Copiar** sugere um nome separado e aceita um destino dentro do volume do jogo. A operação exige o container parado e gravação habilitada no node. O node copia arquivos regulares de até 512 MiB em blocos, preserva os bits comuns de permissão e publica a cópia completa quando o destino está livre. Atualize o node para habilitar a rota de cópia.

**Compactar seleção** reúne arquivos e pastas em um `.tar.gz` novo dentro da pasta atual. A operação exige `file.archive`, o container parado e gravação habilitada no node. O limite é de 40 itens selecionados, até 20 mil entradas dentro deles e 512 MiB descompactados. Links simbólicos e arquivos com múltiplos hard links interrompem a operação. O node preserva a seleção original e só publica o arquivo completo quando o nome está livre. Atualize o node para habilitar a rota de compactação.

**Extrair** aceita `.zip`, `.tar`, `.tar.gz` e `.tgz` de até 512 MiB. A operação exige `file.create`, o container parado e gravação habilitada. Escolha o caminho de uma pasta nova a partir da raiz do jogo. O node limita a saída a 512 MiB e 20 mil arquivos e pastas, verifica o CRC dos arquivos ZIP e recusa links e entradas especiais. O ZIP deve ser de um único volume, sem criptografia e com diretório central convencional. A extração preserva o arquivo compactado e publica a pasta completa quando o destino está livre. [Orientações de ZIP e TAR do .NET](https://learn.microsoft.com/dotnet/standard/io/zip-tar-best-practices)

**Excluir** remove arquivos e pastas selecionados de uma mesma pasta após confirmação. A operação exige `file.delete`, o container parado e `Files:AllowUploads` habilitado no node. O limite é de 40 itens selecionados, 20 mil entradas ao todo e 64 níveis de profundidade. O node percorre a seleção antes de excluir e só remove os links simbólicos em si. Pontos de montagem e arquivos especiais interrompem a operação. Uma falha durante a exclusão pode deixar parte da seleção na pasta. O painel atualiza a listagem após a operação. [Resolução de caminhos no Linux](https://man7.org/linux/man-pages/man2/openat2.2.html)

Na aba **Estatísticas**, o painel mostra os blocos alocados no volume cadastrado. O node reutiliza a medição por 30 segundos, ignora links simbólicos e limita tempo e tamanho da varredura. Se a medição estiver indisponível, o painel exibe um traço. O total de leitura e escrita continua vindo dos contadores do Docker.

## Backups locais

Para habilitar a criação, configure `Files:BackupDirectory` com um diretório privado e gravável pelo UID do node. Monte o diretório em armazenamento persistente, separado dos volumes dos jogos. Mantenha as operações externas de escrita nesses volumes suspensas durante o backup.

Na aba **Backups**, o proprietário ou administrador pode solicitar a criação com o servidor parado. O painel permite uma solicitação pendente por servidor e registra até 20 solicitações por servidor. O worker confirma o estado do container no Docker e mantém o lock de operações do servidor até receber a resposta do node. O histórico mostra o resultado, a data, o tamanho e o SHA-256 do arquivo concluído.

Na aba **Agendamentos**, a tarefa **Backup do volume** usa a mesma fila. Uma sequência **Parar → Backup do volume → Iniciar** espera pelo recibo antes de religar. O estado **Aguardando backup** fica gravado no banco e pode ser retomado por outro worker. Se o backup falhar, a próxima tarefa só é executada quando **Continuar em caso de falha** estiver marcado na tarefa de backup.

As duas telas aceitam exclusões opcionais em sintaxe gitignore, uma por linha. Quando a lista fica vazia, o node lê `.pteroignore` na raiz do volume. Uma lista explícita substitui as regras do arquivo. O limite é de 32 KiB e 256 padrões, com até 16 asteriscos sem escape por padrão. O node recusa regras inválidas ou arquivos de regras que não possa ler com segurança. [Regras de backup do Wings](https://github.com/pterodactyl/wings/blob/develop/server/backup.go) · [Sintaxe e limites de exclusão](https://github.com/pterodactyl/wings/blob/develop/server/filesystem/ignore.go)

O painel usa `POST /v1/servers/{id}/backups/{backupId}/with-exclusions` por mTLS. Atualize o painel e o serviço de arquivos do node para usar os limites atuais. O node cria um TAR/GZip com até 16 GiB de arquivos regulares por padrão. Configure `Files:BackupMaxDataGiB` entre 1 e 64 para ajustar a capacidade de cada node. A criação tem até 30 minutos. A repetição com o mesmo UUID retorna o backup já concluído.

O backup preserva os links simbólicos relativos que apontam diretamente para arquivos ou pastas incluídos no mesmo backup. Se uma regra excluir o destino de um link, inclua o destino ou exclua também o link.

O botão **Baixar backup** envia o arquivo concluído como anexo. O navegador usa uma autorização de uso único, válida por um minuto e vinculada à sessão que a emitiu. Mantenha a interface e a API na mesma origem. O painel transmite o arquivo sem carregá-lo inteiro na memória e interrompe o download se o tamanho ou o SHA-256 divergir do registro. O operador também pode localizar o arquivo em `<BackupDirectory>/<UUID do servidor sem hífens>/<UUID do backup sem hífens>.tar.gz`.

Use **Proteger** para impedir a exclusão de um backup, e **Remover proteção** para voltar a permitir a operação. Backups reservados por um agendamento ou associados a uma restauração também ficam indisponíveis para exclusão. O jogo pode continuar em execução durante a exclusão. O painel libera a vaga na cota quando o node confirma a remoção do arquivo. Se houver falha de comunicação, use **Tentar excluir novamente**.

### Preparação da restauração

Monte um diretório privado, persistente e gravável, separado do armazenamento de backups e dos volumes dos jogos. Configure esse caminho absoluto em `Files:RestoreDirectory` no node e habilite `FileNodes:<UUID do node>:RestoreEnabled` no painel. O botão **Preparar restauração** fica disponível para o proprietário e para administradores em backups concluídos.

O node verifica o SHA-256 e publica a cópia em `<RestoreDirectory>/<UUID do servidor sem hífens>/<UUID do backup sem hífens>/data`. A restauração aceita até 64 GiB de arquivos, 68 GiB comprimidos e 250 mil itens, com prazo de até 45 minutos. O limite configurado em `Files:BackupMaxDataGiB` se aplica à criação de novos arquivos. No Linux, o node extrai os arquivos e pastas antes de conferir e recriar os links internos. O diretório pai contém `receipt.json`. Uma repetição do mesmo pedido devolve o recibo da cópia existente. O jogo pode continuar rodando durante a preparação. O operador usa a cópia preparada para verificar e trocar o volume em uma etapa separada. [Procedimento de recuperação](MIGRACAO_PTERODACTYL_PT-BR.md#recuperar-um-backup-do-arcadenode)
