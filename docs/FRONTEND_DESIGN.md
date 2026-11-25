# Frontend do ArcadeNode

## Direção visual

A referência é o projeto local `application.panel-ptero-master`: IBM Plex Sans, superfícies azul-escuras, acentos violeta/azul, navegação superior compacta e cards com a arte dos jogos. A identidade ArcadeNode e os contratos da API ASP.NET Core foram preservados.

Tokens principais: canvas `#080b14`, superfície `#101626`, superfície elevada `#151d30`, borda `#232c43`, violeta `#b069ff`, azul `#83a8f3`. A fonte é hospedada localmente, com a licença em `frontend/public/fonts/OFL.txt`.

## Organização

- `src/index.css`: fonte, tokens e base global.
- `src/styles/panel.css`: shell, navegação, dashboard, cards, filtros e responsividade.
- `src/styles/server-workspace.css`: navegação lateral, console, métricas, formulários e diálogo de exclusão.
- `src/components/ServerBrowser.tsx`: busca, filtros, grade/lista e estados de carregamento/erro/vazio, compartilhados pelo dashboard e pela listagem.
- `src/components/ServerCard.tsx` e `src/lib/gameVisuals.ts`: identidade visual de cada jogo, com fallback para jogos sem arte.
- `src/components/ui`: status e confirmação com diálogo nativo, foco e cancelamento por teclado.
- `src/pages/ServerDetail.tsx`: operações e consultas reais pelos serviços existentes, com console, estatísticas, configurações e indicação explícita do gerenciador de arquivos ainda em desenvolvimento.

O frontend permanece em React/TypeScript. A aplicação e os serviços de infraestrutura continuam no backend C# existente; não há acoplamento com o PHP do painel de referência.

## Prévia sem infraestrutura

`frontend/.env.local` com `VITE_DEMO_MODE=true` habilita a prévia exclusivamente no Vite de desenvolvimento. Há seis servidores fictícios e dois cartridges para explorar o fluxo de criação. O catálogo de demonstração reflete os jogos que já têm cartridge no repositório.

O adaptador de demonstração não faz requisições para a API, bloqueia alterações/comandos e mantém a sessão fictícia apenas em memória. Os dados não representam servidores reais. No build de produção a prévia não é incluída.

Execute `npm run dev -- --host 127.0.0.1` na pasta `frontend` para visualizar. Para o modo normal, desative a variável e reinicie o Vite.

## Verificação

`npm run build` agora verifica TypeScript antes de gerar o bundle. `npm run typecheck` e `npm run lint` também estão disponíveis. Corrigidas as incompatibilidades de tipagem do formulário dinâmico e os nomes de limites de recursos do assistente.

A validação no navegador cobre busca, filtros, grade/lista, navegação móvel, abas do servidor, fluxo de seleção/configuração do jogo e layouts de 320 a 1440 px. A operação real com Docker e SQL Server continua dependendo da infraestrutura.

## Assets

Minecraft e Project Zomboid: assets já presentes no painel de referência, em `public/themes/howtoo/games`. Arma Reforger: [arte promocional da loja oficial da Bohemia Interactive](https://store.bistudio.com/products/arma-reforger-digital-steam-key), armazenada localmente para evitar dependência de terceiros no carregamento. Marcas e imagens dos jogos pertencem aos respectivos titulares.

## Próxima fase: migração do Pterodactyl

A importação de servidores ainda não foi implementada. Deve ser um módulo separado no backend C#, com inventário do painel de origem, mapeamento de eggs para cartridges, portas/volumes/limites, cópia verificável dos arquivos, validação no destino e corte controlado com possibilidade de retorno. A nova interface não anuncia migrações como uma função disponível.
