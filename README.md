# ArcadeNode

Prototype in active development. Security changes are implemented locally; real SQL/Docker/game acceptance tests are still required. See [the correction report](docs/SECURITY_HARDENING_PT-BR.md). Some game/template features below remain planned.

Troubleshooting: [bilingual warning codes and failure map](docs/DIAGNOSTICS_PT-BR.md), including API logs, browser console and request correlation.

<div align="center">
  <img src="frontend/public/arcadenode.svg" alt="ArcadeNode Logo" width="120" height="120" />
  
  **Game Server Management Panel**
  
  *Host and manage your game servers with ease*
  
  [![License](https://img.shields.io/badge/license-MIT-purple.svg)](LICENSE)
  [![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
  [![React](https://img.shields.io/badge/React-18-61dafb)](https://react.dev/)
</div>

---

## 🎮 Supported Games

<table>
  <tr>
    <td align="center">
      <img src="https://img.shields.io/badge/Project%20Zomboid-4ade80?style=for-the-badge&logo=steam&logoColor=white" alt="Project Zomboid"/>
      <br/>Survival Horror
    </td>
    <td align="center">
      <img src="https://img.shields.io/badge/Arma%20Reforger-f97316?style=for-the-badge&logo=steam&logoColor=white" alt="Arma Reforger"/>
      <br/>Military Simulation
    </td>
    <td align="center">
      <img src="https://img.shields.io/badge/Minecraft%20Java-84cc16?style=for-the-badge&logo=minecraft&logoColor=white" alt="Minecraft Java"/>
      <br/>Sandbox Adventure
    </td>
    <td align="center">
      <img src="https://img.shields.io/badge/Minecraft%20Bedrock-a855f7?style=for-the-badge&logo=minecraft&logoColor=white" alt="Minecraft Bedrock"/>
      <br/>Cross-Platform
    </td>
  </tr>
</table>

## ✨ Features

| Feature | Description |
|---------|-------------|
| 🎮 **Game Server Management** | Create, start, stop, and restart game servers with one click |
| 📊 **Real-time Monitoring** | Live CPU, memory, disk, and network usage statistics |
| 🖥️ **Console Access** | View logs and send commands directly to your servers |
| 👥 **User Management** | Role-based access control (User, SubUser, Admin, SuperAdmin) |
| 🔧 **Node Management** | Manage multiple Docker nodes across your infrastructure |
| 🥚 **Game Templates** | Pre-configured templates for Project Zomboid, Arma Reforger, Minecraft Java & Bedrock |
| 🔐 **Secure Authentication** | JWT-based authentication with BCrypt password hashing |
| 🌐 **Modern UI** | Beautiful dark theme with responsive design |

## 🛠️ Tech Stack

- **Backend**: .NET 10 with ASP.NET Core Web API
- **Database**: SQL Server 2025
- **Frontend**: React 18 + TypeScript + Vite + TailwindCSS
- **Containerization**: Docker & Docker Compose

## 🚀 Quick Start

### Production setup

Follow [deploy/README.md](deploy/README.md) before starting containers. The panel requires external private SQL Server, separate migration/runtime identities, TLS certificates, secret files and operator-enrolled Docker nodes with mTLS and verified disk quotas.

The Compose file publishes only HTTPS through Nginx. It contains no database, plaintext daemon or host Docker socket. Configuration is in [.env.example](.env.example); it contains file paths instead of passwords. The HTTP process does not migrate the database or print administrator credentials. Initial provisioning uses an explicit maintenance command.

Read the [security corrections and acceptance limits](docs/SECURITY_HARDENING_PT-BR.md). Existing JWTs/invitations are invalidated by the security migration; legacy containers require deliberate reprovisioning. Remote installers, standalone launch wizards and password-reset shortcuts have been retired.


## 💻 Development Setup

### Backend (.NET 10)

```bash
cd backend/ServerPanel.API

# Restore packages
dotnet restore

# Configure local SQL/JWT secret files first; see deploy/README.md.
# Apply migrations explicitly with the maintenance identity.
dotnet watch run
```

### Frontend (React)

```bash
cd frontend

# Install dependencies
npm ci

# Run development server
npm run dev
```

## 📁 Project Structure

```
ArcadeNode/
├── 📄 docker-compose.yml      # Docker orchestration
├── � .env.example             # Environment template
├── 📂 scripts/                 # Deployment & management scripts
│   ├── 📄 deploy.ps1           # Full deployment (PowerShell)
│   ├── 📄 deploy.sh            # Full deployment (Bash)
│   ├── 📄 quick-deploy.ps1     # Retired legacy entrypoint
│   ├── 📄 deploy-wizard.sh     # Retired legacy entrypoint
│   └── 📄 init-setup.sh        # Retired legacy entrypoint
├── 📂 backend/
│   ├── 🐳 Dockerfile
│   └── 📂 ServerPanel.API/
│       ├── 📂 Controllers/    # API endpoints
│       ├── 📂 Data/           # EF Core DbContext
│       ├── 📂 DTOs/           # Data transfer objects
│       ├── 📂 Models/         # Entity models
│       ├── 📂 Services/       # Business logic
│       └── 📄 Program.cs      # Application entry
├── 📂 frontend/
│   ├── 🐳 Dockerfile
│   ├── 📄 nginx.conf
│   └── 📂 src/
│       ├── 📂 components/     # React components
│       ├── 📂 pages/          # Page components
│       ├── 📂 services/       # API services
│       ├── 📂 stores/         # Zustand state stores
│       └── 📂 types/          # TypeScript types
├── 📂 cartridges/              # Game server cartridge definitions
│   ├── 📂 arma-reforger/
│   ├── 📂 minecraft-bedrock/
│   ├── 📂 minecraft-java/
│   └── 📂 project-zomboid/
├── 📂 game-servers/            # Game server Docker configs
│   ├── 📂 project-zomboid/    # PZ dedicated server
│   ├── 📂 arma-reforger/      # Reforger dedicated server
│   ├── 📂 minecraft/          # MC Java (Paper/Vanilla)
│   ├── 📂 minecraft-bedrock/  # MC Bedrock server
│   └── 📂 wizards/            # Game deployment wizard modules
└── 📂 docs/                    # Documentation
```

## 🔌 API Reference

### Authentication

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/auth/login` | User login |
| `POST` | `/api/auth/register` | User registration |
| `GET` | `/api/auth/me` | Get current user |
| `PUT` | `/api/auth/me` | Update profile |
| `POST` | `/api/auth/change-password` | Change password and revoke sessions |
| `POST` | `/api/auth/logout` | Revoke current session |

### Servers

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/servers` | List all servers |
| `GET` | `/api/servers/{id}` | Get server details |
| `POST` | `/api/servers` | Create server (Admin) |
| `PUT` | `/api/servers/{id}` | Edit name/description only |
| `DELETE` | `/api/servers/{id}` | Delete server (Admin) |
| `POST` | `/api/servers/{id}/power` | Power action |
| `GET` | `/api/servers/{id}/logs` | Get console logs |
| `GET` | `/api/servers/{id}/logs/live` | Stream a bounded console window with bearer authentication |
| `POST` | `/api/servers/{id}/command` | Send command |
| `GET` | `/api/servers/{id}/stats` | Get resource stats |

### Server schedules

Server schedules are available under `/api/servers/{serverId}/schedules`. Owners and administrators can list or create schedules, set activity with `PUT /{scheduleId}/activity`, and delete with `DELETE /{scheduleId}`. Schedules use five-field UTC cron and accept up to five ordered console or power tasks with an optional delay. Apply the `IndexPendingSchedules` migration before enabling the worker. Set `Schedules__Enabled=false` to pause execution for maintenance. The worker advances the next occurrence before issuing a task. Interrupted runs are skipped. [Cronos](https://github.com/HangfireIO/Cronos)

### Nodes (Admin)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/nodes` | List nodes |
| `POST` | `/api/nodes` | Create node |
| `PUT` | `/api/nodes/{id}` | Update node |
| `DELETE` | `/api/nodes/{id}` | Delete node |
| `GET` | `/api/nodes/{id}/allocations` | Get allocations |
| `POST` | `/api/nodes/{id}/allocations` | Create allocations |

### Admin

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/admin/dashboard` | Dashboard stats |
| `GET` | `/api/admin/users` | List users |
| `DELETE` | `/api/admin/users/{id}` | Delete user |
| `GET` | `/api/admin/nests` | List game categories |
| `GET` | `/api/admin/eggs` | List game templates |
| `GET`, `POST` | `/api/admin/egg-catalog/nests` | List or create categories for imported manifests |
| `GET` | `/api/admin/egg-catalog/nests/{nestId}/eggs` | List imported egg drafts |
| `POST` | `/api/admin/egg-catalog/nests/{nestId}/eggs/import` | Import a Pterodactyl PTDL v1 or v2 JSON manifest |
| `GET` | `/api/admin/egg-catalog/eggs/{id}/export` | Download the imported manifest |

See the [Pterodactyl migration preparation guide](docs/MIGRACAO_PTERODACTYL_PT-BR.md) for the source inventory and egg catalog.

## ⚙️ Environment Variables

### Backend

| Variable | Description | Default |
|----------|-------------|---------|
| `ConnectionStrings__DefaultConnection` | SQL Server connection | - |
| `Jwt__KeyFile` | File containing a random signing key, at least 48 bytes | Required |
| `Database__ConnectionStringFile` | File containing verified TLS SQL connection | Required |
| `Jwt__Issuer` | JWT issuer | ArcadeNode |
| `Jwt__Audience` | JWT audience | ArcadeNodeUsers |
| `NodeConfigurationPath` | Operator-managed node/image enrollment JSON | Required for nodes |
| `LocalAssistant__Enabled` | Enables the optional Ollama assistant | `false` |
| `LocalAssistant__Endpoint` | Fixed private Ollama HTTP(S) origin | Required when enabled |
| `LocalAssistant__Model` | Installed local Ollama model and tag | Required when enabled |
| `LocalAssistant__TimeoutSeconds` | Request deadline in seconds, from 10 to 120, including an optional runtime sample | `30` |
| `LocalAssistant__MaxOutputTokens` | Maximum response tokens, from 16 to 600 | `600` |

The server **Assistente** tab uses the selected Ollama model. In Compose deployments, place the `LocalAssistant` section in the private JSON loaded by `NodeConfigurationPath`. Run Ollama with a local model on a private network reachable by the API. Set `OLLAMA_NO_CLOUD=1` on the Ollama service for local-only operation. The API sends the question, server name, last known status, and configured resource limits. Users can opt in to a current sample from that server's enrolled node. The sample includes the observed state, CPU and memory measurements when available, and exit information for a stopped container. The assistant can answer from panel configuration when the node has no available sample. Model responses are shown as plain text. [Ollama chat API](https://docs.ollama.com/api/chat) · [Local-only configuration](https://docs.ollama.com/faq#how-do-i-disable-ollamas-cloud-features)

To offer CPU affinity in the deploy wizard, set `AllowedCpuCores` to the logical CPU IDs available on each enrolled Docker node. Apply the `ServerCpuAffinity` migration first. The optional selection restricts which logical CPUs the container can run on while its CPU quota still applies. Servers may share a logical CPU.

### Frontend

| Variable | Description | Default |
|----------|-------------|---------|
| `VITE_API_URL` | Backend API URL | /api |

## 🎮 Game Server Templates

### Project Zomboid
- Dedicated server with mod support
- Customizable world settings
- Steam Workshop integration

### Arma Reforger
- Official dedicated server
- Mod support via Workshop
- Mission file management

### Minecraft Java Edition
- Paper/Spigot/Vanilla support
- Plugin management
- World backup system
- Optimized with Aikar's flags

### Minecraft Bedrock Edition
- Official Bedrock Dedicated Server
- Cross-platform (Xbox, Mobile, Windows 10/11)
- Server-authoritative anti-cheat
- Resource & behavior pack support

## 🗄️ Database

The panel uses SQL Server 2025 with Entity Framework Core. Database migrations run only through the explicit maintenance command described in deploy/README.md.

### Models
- **User** - Panel users with roles
- **GameServer** - Game server instances
- **Node** - Docker nodes for hosting servers
- **Allocation** - IP:Port allocations
- **Nest** - Game categories (Survival, Military, Sandbox)
- **Egg** - Game server templates with startup configs
- **Backup** - Server backups
- **Schedule** - Scheduled tasks

## Security

- **JWT Authentication** - Secure token-based auth
- **BCrypt Hashing** - Industry-standard password hashing
- **Role-Based Access** - Granular permission system
- **CORS Policy** - Controlled cross-origin access

## 📄 License

MIT License

---

<div align="center">
  <strong>ArcadeNode</strong> — Built with 💜 for gamers
</div>
