# Leitura inicial do ArcadeNode

Data: 16/11/2026. Base: branch `main`, commit `9bd4f9a`.

Atualização após a reformulação visual: frontend reorganizado conforme `FRONTEND_DESIGN.md`; build passa com checagem TypeScript e lint passa. As contagens de erros abaixo registram a situação inicial anterior às correções. Infraestrutura e funcionalidades de backend pendentes continuam exigindo validação real.

Esta é uma revisão de arquitetura, implementação e preparação local. A auditoria de segurança aprofundada e os testes reais de infraestrutura ficam como próxima etapa. A compilação não comprova o funcionamento com banco, Docker ou servidores de jogos.

## Ambiente e execução

- Repositório clonado diretamente na pasta aberta.
- Frontend disponível em http://localhost:3000, com Vite vinculado a `127.0.0.1`.
- Para iniciar novamente: `cd frontend` e `npm run dev -- --host 127.0.0.1`.
- Dependências instaladas. Corrigido o conflito de peers entre Vite 8 e `@vitejs/plugin-react` 4: plugin atualizado para `^5.2.0`, com lockfile atualizado.
- API não iniciada: depende de SQL Server e de configuração de JWT. Docker não está disponível no PATH deste ambiente.
- Sem API, login e cadastro podem ser visualizados, mas não autenticam. As rotas internas exigem uma sessão real na execução normal.
- Prévia do dashboard: `VITE_DEMO_MODE=true` em `frontend/.env.local` (ignorado pelo Git), apenas em desenvolvimento. Dados fictícios em memória, sem persistir sessão ou chamar a API; mutações bloqueadas. Desativar a variável e reiniciar o Vite restaura o modo normal. Build de produção não ativa a prévia.
- Nenhum deploy remoto, alteração de credenciais, commit ou push realizado.

## Estrutura e fluxos

| Área | Implementação encontrada |
| --- | --- |
| Frontend | React 18, TypeScript, Vite, Tailwind, TanStack Query e Zustand. Login, cadastro por convite, dashboard, servidores, console, estatísticas, perfil, nós, usuários e assistente de deploy. |
| API | ASP.NET Core / EF Core 9, SQL Server, JWT e BCrypt. Controllers de autenticação, administração, servidores, nós, templates e cartridges. |
| Persistência | Duas migrations, seed de administrador em runtime e seed de nest/egg Minecraft. Modelos adicionais de backups, schedules, API keys e auditoria. |
| Provisionamento | Fluxo antigo por GameTemplateService/DeploymentController e fluxo novo por CartridgeService/ServersController. O assistente atual usa o novo. |
| Jogos | Dockerfiles, entrypoints, configurações e wizards para Zomboid, Arma Reforger, Minecraft Java e Bedrock. Apenas Java e Zomboid têm cartridge.json. |
| Operação | Compose com SQL Server, API, Nginx e Docker-in-Docker; scripts de build, SSH, sincronização, instalação e manutenção. |
| CI | Workflow de CodeQL. Não foram encontrados projetos ou arquivos de testes automatizados nem pipeline de build/lint/typecheck. |

## Verificações executadas

| Verificação | Resultado |
| --- | --- |
| Instalação original com npm ci | Falhou por incompatibilidade de peers Vite/plugin React. |
| Instalação após ajuste do plugin | Passou com npm install e npm ci, com servidor parado para liberar o binário nativo no Windows. |
| npm run build | Passou. Aviso sobre uso de `__dirname` em vite.config.ts. |
| npm run lint | Falhou: 5 erros e 2 avisos. |
| npx tsc --noEmit -p tsconfig.json | Falhou: 21 erros. |
| dotnet build Server_Managet_Panel.sln --nologo | Passou: 0 avisos e 0 erros. |
| Navegador com Playwright | Redirecionamento para login, título, campo de senha, link de cadastro e campo de convite verificados; sem erros de console nessas páginas. |
| Banco, API em execução, Docker e jogos | Não validados em runtime. |

O script `build` executa apenas `vite build`; por isso gera bundle mesmo com erros de TypeScript.

## Pendências prioritárias

1. **Preparar a infraestrutura local completa.** Disponibilizar Docker/SQL Server, gerar configuração local de JWT e seed, subir API, aplicar migrations e testar uma sessão real. Configurar `CartridgesPath` para a pasta local: appsettings.json aponta para `/app/cartridges`, caminho do contêiner.
2. **Alinhar contratos do assistente de deploy.** `frontend/src/pages/admin/DeployWizard.tsx` usa `min`/`max`, mas API e tipos expõem `minimum`/`maximum`. O assistente consulta `resources.disk`, ausente no DTO de resposta e nos tipos. Há também inconsistência entre CPU em cores nos JSONs e porcentagem no servidor/UI. Referências: `frontend/src/types/index.ts`, `backend/ServerPanel.API/DTOs/CartridgeDTOs.cs`, `cartridges/*/cartridge.json`.
3. **Resolver os erros estáticos.** `DynamicVariableForm.tsx`: declarações dentro de case sem bloco, propriedades `hidden`/`icon` ausentes nos contratos e valores null passados a atributos HTML. `DeployWizard.tsx`: import sem uso, limites de recursos incompatíveis e objeto unknown renderizado como ReactNode. `ServerDetail.tsx`: dois avisos de dependências de effects.
4. **Conectar as operações ao nó escolhido.** `DockerService` obtém apenas `clientFactory.GetClient()` sem endpoint. A factory aceita endpoints, mas criação, energia, logs e estatísticas continuam usando o cliente padrão. O cadastro de múltiplos nós ainda não resulta em provisionamento independente por nó.
5. **Aplicar portas, volumes e diretório de execução dos cartridges.** `DockerService.CreateContainerAsync` publica apenas a porta principal em TCP/UDP e monta um volume em `/data`; os jogos e cartridges definem portas adicionais e diretórios em `/home/...`. Conferir persistência real após recriação do contêiner e acesso a RCON/portas secundárias.
6. **Unificar imagens e catálogo.** `cartridges/minecraft-java/cartridge.json` usa `arcadenode/minecraft-java:latest`; `game-servers/docker-compose.yml` produz `arcadenode/minecraft:latest`. Criar cartridges de Arma e Bedrock. Os cartões do dashboard usam templates antigos e enviam `?game=...`, que o novo DeployWizard não lê.
7. **Completar o console.** `DockerService.SendCommandAsync` executa um shell separado no contêiner, sem encaminhar texto ao stdin/RCON do jogo. Comandos como `list`, `save` e `players` precisam de transporte apropriado; vários comandos definidos nos cartridges também usam aspas rejeitadas pela validação atual.
8. **Aplicar limites consistentemente.** A criação antiga verifica capacidade de memória/disco, enquanto `CreateServerFromCartridgeAsync` não repete a verificação. Revisar atualização de recursos, concorrência na reserva de portas e integração com allocations. `DiskLimit` não é aplicado ao contêiner; validar o mecanismo de CPU no ambiente Linux usado.
9. **Corrigir semântica de falhas e estados.** Falha de criação do contêiner pode retornar servidor com status Error e HTTP 201. Power e command podem retornar HTTP 200 com success=false, que o frontend trata como sucesso. Atualização de configurações altera o registro, sem atualizar o contêiner existente. Dessuspensão não restaura o status Suspended. Status/progresso de deploy é calculado por enum, sem confirmação de prontidão do jogo.
10. **Implementar funcionalidades ainda parciais.** Gerenciador de arquivos exibe “coming soon”. Backups, agendamentos e API keys têm modelos, mas não há fluxo completo de controllers/serviços/UI. SubUser existe como papel, mas o acesso a servidores considera administrador ou proprietário; faltam permissões de compartilhamento. Recursos avançados de cartridges, como atualização automática e configuração de arquivos, precisam de execução integrada.
11. **Adicionar validação automatizada ao CI.** Instalação por lockfile, lint, typecheck, build da API/frontend e testes de integração de autenticação, provisionamento e persistência. Priorizar testes dos contratos e dos comportamentos acima.
12. **Atualizar documentação e configuração de deploy.** Os relatórios de março descrevem problemas já corrigidos, como segredo JWT fallback, ausência de rate limiting, headers e convites. `CARTRIDGE_SYSTEM.md` também lista como pendentes itens já implementados. README anuncia licença MIT, mas não foi encontrado arquivo LICENSE. Variáveis VITE são lidas no build; environment do contêiner Nginx não reconfigura o bundle já gerado. Os ícones externos dos cartridges precisam ser conciliados com a CSP `img-src 'self' data:`.

## Pontos para o próximo checkout de segurança

Revalidar no código atual e depois em runtime, sem reutilizar as contagens de severidade dos relatórios antigos:

- Autorização por proprietário no status de deployment; o método atual busca por ID sem checagem equivalente a CanAccessServer.
- Permissões sobre atualização de recursos/startup por usuários comuns e revogação de JWT após mudança de senha, papel ou desativação.
- CSRF no primeiro request autenticado após login e política de cookies/HTTPS no proxy.
- Endpoint Docker de teste, destinos permitidos e isolamento entre nós.
- Exposição da porta 2375 sem TLS, Docker-in-Docker privilegiado, montagem do socket, imagens permitidas e permissões dos volumes.
- Consumo concorrente de convites, rate limit compartilhado, paginação sem limite por padrão e limites de requisição/logs.
- Segredos padrão nas imagens de jogos, substituição de variáveis em shell/startup e proteção de credenciais nos scripts.

## Ordem sugerida

1. Infraestrutura local e contratos/typecheck/lint.
2. Deploy real de um jogo: nó, imagem, portas, limites, persistência e console.
3. Auditoria de segurança com testes autenticados e perfis distintos.
4. Expansão do catálogo e funcionalidades adicionais.
