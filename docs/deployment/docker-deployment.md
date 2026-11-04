# Production Docker Deployment Guide

This guide provides step-by-step instructions for deploying LocalRoast in production using Docker and Docker Compose on any Linux host, VPS, homelab, or cloud VM (Ubuntu, Debian, Fedora, Arch, etc.).

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Prerequisites](#prerequisites)
3. [Quick Start](#quick-start)
4. [Production Docker Compose Configuration](#production-docker-compose-configuration)
5. [Environment Configuration Reference](#environment-configuration-reference)
6. [Database Persistence & Automated Migrations](#database-persistence--automated-migrations)
7. [Reverse Proxy & SSL / TLS Configuration](#reverse-proxy--ssl--tls-configuration)
   - [Nginx](#nginx)
   - [Caddy](#caddy)
   - [Traefik](#traefik)
   - [Cloudflare Tunnel](#cloudflare-tunnel)
8. [Database Backup & Restore](#database-backup--restore)
9. [Updating LocalRoast](#updating-localroast)
10. [Troubleshooting & Logs](#troubleshooting--logs)

---

## Architecture Overview

LocalRoast's production container architecture is designed to be streamlined, lightweight, and robust:

- **`local-roast-api` (Unified Application Container)**:
  - Serves the compiled Angular Single Page Application (SPA) from static files.
  - Hosts the ASP.NET Core 10 Web API and OpenAPI / Scalar documentation endpoints.
  - Listens on internal port `8080` (mapped to a randomized host port such as `62781` so multiple apps deployed on the same host never collide).
  - Automatically verifies and applies pending database schema migrations on container boot.
- **`local-roast-sqlserver` (Database Container)**:
  - Microsoft SQL Server 2022 Linux container (`mcr.microsoft.com/mssql/server:2022-latest`).
  - Persists all relational data and user credentials in a persistent named volume or host bind-mount.

```text
┌────────────────────────────────────────────────────────┐
│                     Client Browser                     │
└───────────────────────────┬────────────────────────────┘
                            │ HTTPS (443)
┌───────────────────────────▼────────────────────────────┐
│          Reverse Proxy (Nginx / Caddy / Traefik)       │
└───────────────────────────┬────────────────────────────┘
                            │ HTTP (Randomized Port: 62781)
┌───────────────────────────▼────────────────────────────┐
│          LocalRoast Container (SPA + API)       │
└───────────────────────────┬────────────────────────────┘
                            │ SQL TDS (1433)
┌───────────────────────────▼────────────────────────────┐
│             Microsoft SQL Server Container             │
│                 (Persistent Storage)                   │
└────────────────────────────────────────────────────────┘
```

---

## Prerequisites

- **Operating System**: Linux (Ubuntu 22.04+, Debian 12+, Rocky/RHEL 9+) with root or `sudo` access.
- **Docker Engine**: Docker 24.0+ installed.
- **Docker Compose**: Docker Compose v2.20+ (`docker compose` CLI plugin).
- **Hardware Requirements**:
  - Minimum: 2 CPU cores, 2 GB RAM (SQL Server requires at least 2 GB RAM).
  - Recommended: 4 CPU cores, 4 GB+ RAM.
- **A Domain Name**: Pointing to your server's IP address (e.g., `app.yourdomain.com`).

---

## Quick Start

### 1. Create Application Directory

You do not need to clone the full source code repository to run LocalRoast in production. Create an application directory on your server:

```bash
mkdir -p /opt/localroast && cd /opt/localroast
```

### 2. Configure Environment Variables (`.env`)

Create a `.env` file:

```bash
nano .env
```

Generate secure secrets for the SQL Server SA account and the JWT signing key:

```bash
# Generate a 32+ character random secret
openssl rand -base64 32
```

Configure your `.env` file:

```env
# Database SA Password (must include uppercase, lowercase, numbers, and special characters)
MSSQL_SA_PASSWORD=YourStrongDatabasePassword123!_MustIncludeCapsAndSpecial

# LocalRoast Security Settings (at least 32 characters)
JWT_SECRET=ReplaceWithYourGeneratedBase64SecretKeyAtLeast32CharsLong!

# Randomized Application Host Port Mapping (assigned per-app to avoid port conflicts across services)
APP_PORT=62781

# Optional: Hostname for Reverse Proxy / CORS
APP_HOST=app.yourdomain.com
```

### 3. Create `docker-compose.yml`

Create your `docker-compose.yml` file:

```bash
nano docker-compose.yml
```

Paste the production configuration:

```yaml
name: local-roast-prod

services:
  api:
    image: ghcr.io/tylersmart/localroast:latest
    container_name: local-roast-api
    restart: unless-stopped
    ports:
      # Maps a randomized external host port (e.g. 62781) to internal container port 8080
      - "${APP_PORT:-62781}:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_HTTP_PORTS: 8080
      ConnectionStrings__DefaultConnection: "Server=sqlserver;Database=LocalRoast;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True;"

      # JWT Authentication Secret
      Authentication__Jwt__Secret: "${JWT_SECRET}"
      Authentication__Jwt__Issuer: "LocalRoast"
      Authentication__Jwt__Audience: "LocalRoast"
      Authentication__Jwt__ExpirationMinutes: 1440

      # Optional: External OIDC / SSO (Google, Authentik, Keycloak, Entra ID)
      # Authentication__Oidc__Enabled: "true"
      # Authentication__Oidc__ProviderName: "Google"
      # Authentication__Oidc__Authority: "https://accounts.google.com"
      # Authentication__Oidc__ClientId: "your-client-id.apps.googleusercontent.com"
      # Authentication__Oidc__ClientSecret: "your-client-secret"
      # Authentication__Oidc__Scope: "openid profile email"
      # Authentication__Oidc__AuthorizationEndpoint: "https://accounts.google.com/o/oauth2/v2/auth"
      # Authentication__Oidc__TokenEndpoint: "https://oauth2.googleapis.com/token"
      # Authentication__Oidc__UserInfoEndpoint: "https://openidconnect.googleapis.com/v1/userinfo"
    volumes:
      # Persistent storage for recipe photos, PDFs, attachments, and thumbnails
      - uploads_data:/data/uploads
    depends_on:
      sqlserver:
        condition: service_healthy

  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: local-roast-sqlserver
    restart: unless-stopped
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "${MSSQL_SA_PASSWORD}"
      MSSQL_PID: "Developer" # Options: Developer, Express, Standard
    volumes:
      - sqlserver_data:/var/opt/mssql
    healthcheck:
      test:
        [
          "CMD-SHELL",
          "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"${MSSQL_SA_PASSWORD}\" -C -Q \"SELECT 1\" -b -o /dev/null",
        ]
      interval: 5s
      timeout: 5s
      retries: 24
      start_period: 20s

volumes:
  sqlserver_data:
  uploads_data:
```

> [!NOTE]
> **Docker Named Volumes vs. Host Bind Mounts (e.g. TrueNAS)**:
> In standard Docker, `sqlserver_data:/var/opt/mssql` and `uploads_data:/data/uploads` are Docker-managed named volumes, which requires declaring `volumes: [sqlserver_data, uploads_data]` at the bottom of the Compose file. On systems like TrueNAS Scale, host path bind-mounts (e.g. `/mnt/tank/localroast/db:/var/opt/mssql` and `/mnt/tank/localroast/uploads:/data/uploads`) are used instead to leverage ZFS datasets and automated snapshots, which does not require a top-level `volumes:` section. See the [TrueNAS Scale Deployment Guide](truenas-scale.md) for details.

> [!TIP]
> If you prefer to build the container from source code instead of pulling the prebuilt image from GHCR, clone the repository and add a `build:` block to the `api` service:
> ```yaml
> build:
>   context: .
>   dockerfile: API/Dockerfile
> ```

### 4. Launch the Application

Start the services:

```bash
docker compose up -d
```

Monitor the startup logs to verify that the database healthcheck passes and migrations are automatically executed:

```bash
docker compose logs -f api
```

You should see log output confirming:

```text
Database migration complete.
Now listening on: http://[::]:8080
Application started. Press Ctrl+C to shut down.
```

Open `http://<your-server-ip>:62781` in your web browser. You should see the LocalRoast sign-in page!

---

## Environment Configuration Reference

The following environment variables can be customized in `.env` or passed directly to the `api` container:

| Variable                                      | Default               | Description                                                                  |
| --------------------------------------------- | --------------------- | ---------------------------------------------------------------------------- |
| `ASPNETCORE_ENVIRONMENT`                      | `Production`          | ASP.NET Core environment mode. Always set to `Production`.                   |
| `ASPNETCORE_HTTP_PORTS`                       | `8080`                | Internal listening port inside the container.                                |
| `ConnectionStrings__DefaultConnection`        | Required              | ADO.NET connection string pointing to SQL Server.                            |
| `Authentication__Jwt__Secret`                 | Required              | Secret signing key for JWT tokens. Must be >= 32 characters.                 |
| `Authentication__Jwt__Issuer`                 | `LocalRoast`      | Token issuer claim.                                                          |
| `Authentication__Jwt__Audience`               | `LocalRoast`      | Token audience claim.                                                        |
| `Authentication__Jwt__ExpirationMinutes`      | `1440` (24 hours)     | Session token validity duration in minutes.                                  |
| `Authentication__Oidc__Enabled`               | `false`               | Set to `true` to enable external Single Sign-On.                             |
| `Authentication__Oidc__ProviderName`          | `OIDC`                | Display label on the login screen (e.g. `Google`, `Authentik`, `Keycloak`).  |
| `Authentication__Oidc__Authority`             | -                     | OIDC Identity Provider discovery authority URL.                              |
| `Authentication__Oidc__ClientId`              | -                     | OAuth2 Client ID from your provider.                                         |
| `Authentication__Oidc__ClientSecret`          | -                     | OAuth2 Client Secret from your provider.                                     |
| `Authentication__Oidc__Scope`                 | `openid profile email`| Requested OAuth2 scopes.                                                     |
| `Authentication__Oidc__AuthorizationEndpoint` | (Auto-derived)        | Custom authorization endpoint URL (optional).                                |
| `Authentication__Oidc__TokenEndpoint`         | (Auto-derived)        | Custom token exchange URL (optional).                                        |
| `Authentication__Oidc__UserInfoEndpoint`      | (Auto-derived)        | Custom user info endpoint URL (optional).                                    |

> [!TIP]
> For complete step-by-step instructions on setting up Google, Authentik, Keycloak, or Entra ID SSO, see the **[OIDC & OAuth2 Authentication Guide](oidc-authentication.md)**.

---

## Database Persistence & Automated Migrations

LocalRoast uses Entity Framework Core with automatic startup migrations:

1. When the `local-roast-api` container boots, it checks the database specified in `ConnectionStrings__DefaultConnection`.
2. If the `LocalRoast` database does not exist, it creates it automatically.
3. It evaluates pending migrations in `Data/Migrations` and executes any unapplied schema updates.
4. Database tables, indexes, CRDT snapshots, revision history, and user accounts are fully managed without requiring manual SQL script execution.

All database files reside inside `/var/opt/mssql` inside the `sqlserver` container and are persisted across updates in the `sqlserver_data` Docker volume.

---

## Reverse Proxy & SSL / TLS Configuration

To securely serve LocalRoast with HTTPS and support real-time WebSocket collaboration, place a reverse proxy in front of the container.

### Nginx

Create `/etc/nginx/sites-available/localroast.conf`:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name diagrams.yourdomain.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name diagrams.yourdomain.com;

    ssl_certificate /etc/letsencrypt/live/diagrams.yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/diagrams.yourdomain.com/privkey.pem;

    client_max_body_size 50M;

    location / {
        proxy_pass http://127.0.0.1:62781;
        proxy_http_version 1.1;

        # WebSocket support for SignalR real-time collaboration
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Disable proxy buffering for low-latency collaboration updates
        proxy_buffering off;
        proxy_read_timeout 86400s;
        proxy_send_timeout 86400s;
    }
}
```

Enable the site and reload Nginx:

```bash
sudo ln -s /etc/nginx/sites-available/localroast.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

---

### Caddy

Add the following block to your `/etc/caddy/Caddyfile`:

```caddy
diagrams.yourdomain.com {
    reverse_proxy 127.0.0.1:62781 {
        # Caddy automatically proxies WebSockets out of the box
        header_up X-Forwarded-Proto {scheme}
    }
}
```

Reload Caddy:

```bash
sudo systemctl reload caddy
```

---

### Traefik

If running Traefik as your Docker edge router, add labels to the `api` service in `docker-compose.yml`:

```yaml
services:
  api:
    # ...
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.localroast.rule=Host(`diagrams.yourdomain.com`)"
      - "traefik.http.routers.localroast.entrypoints=websecure"
      - "traefik.http.routers.localroast.tls.certresolver=myresolver"
      - "traefik.http.services.localroast.loadbalancer.server.port=8080"
```

---

### Cloudflare Tunnel

If using `cloudflared` (Zero Trust Tunnel):

1. In the Cloudflare Zero Trust Dashboard, navigate to **Networks** -> **Tunnels**.
2. Add a Public Hostname:
   - Subdomain: `diagrams`
   - Domain: `yourdomain.com`
   - Service Type: `HTTP`
   - URL: `localhost:62781` (or `local-roast-api:8080` if in same Docker network)
3. Under **Additional application settings** -> **HTTP Settings**, enable **No TLS Verify** (if applicable) and ensure **WebSockets** are enabled on your Cloudflare zone.

---

## Database Backup & Restore

### Backing Up the Database

You can take a clean SQL Server backup using `sqlcmd` directly inside the container without stopping the service:

```bash
docker exec -t local-roast-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "${MSSQL_SA_PASSWORD}" -C \
  -Q "BACKUP DATABASE [LocalRoast] TO DISK = N'/var/opt/mssql/data/LocalRoast_backup.bak' WITH NOFORMAT, NOINIT, SKIP, NOREWIND, NOUNLOAD, STATS = 10"
```

Then copy the backup file to your host backup directory:

```bash
docker cp local-roast-sqlserver:/var/opt/mssql/data/LocalRoast_backup.bak /backups/localroast_$(date +%F).bak
```

### Restoring the Database

1. Copy the backup file into the SQL Server container:
   ```bash
   docker cp /backups/localroast_backup.bak local-roast-sqlserver:/var/opt/mssql/data/restore.bak
   ```
2. Execute the restore command:
   ```bash
   docker exec -t local-roast-sqlserver /opt/mssql-tools18/bin/sqlcmd \
     -S localhost -U sa -P "${MSSQL_SA_PASSWORD}" -C \
     -Q "RESTORE DATABASE [LocalRoast] FROM DISK = N'/var/opt/mssql/data/restore.bak' WITH REPLACE"
   ```
3. Restart the `local-roast-api` container to re-initialize connection pools:
   ```bash
   docker compose restart api
   ```

---

## Updating LocalRoast

To update to a new version:

```bash
cd /opt/localroast

# 1. Pull latest image from GHCR
docker compose pull

# 2. Re-create the container with zero downtime
docker compose up -d

# 3. Verify logs
docker compose logs -f api
```

The database container remains untouched with all data preserved in `sqlserver_data`, and any new EF Core migrations are automatically applied on startup.

---

## Troubleshooting & Logs

### Viewing Logs

```bash
# View combined logs
docker compose logs -f

# View API logs only
docker compose logs -f api

# View SQL Server logs only
docker compose logs -f sqlserver
```

### Common Issues

1. **SQL Server container exiting with code 1**:
   - Cause: The SA password does not meet Microsoft SQL Server complexity requirements (must contain uppercase, lowercase, numbers, and special symbols; minimum 8 characters).
   - Fix: Update `MSSQL_SA_PASSWORD` in `.env` to a compliant password and restart.
2. **Container running out of memory**:
   - Cause: Host has less than 2 GB RAM available for SQL Server.
   - Fix: Ensure the host has at least 2 GB free memory or allocate swap space.
3. **WebSockets disconnecting frequently**:
   - Cause: Reverse proxy timeout is too short for idle persistent connections.
   - Fix: Increase `proxy_read_timeout` to `86400s` in Nginx, or enable keep-alive pings.
4. **Port 62781 already in use**:
   - Fix: Change `APP_PORT=8585` in `.env` and update your reverse proxy `proxy_pass` to match.
