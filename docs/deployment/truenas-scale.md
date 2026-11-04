# TrueNAS Scale Deployment Guide

This guide provides step-by-step instructions for deploying LocalRoast on **TrueNAS Scale** (supporting both **Electric Eel 24.10+** native Docker Compose and **Dragonfish 24.04** Custom Apps).

---

## Table of Contents

1. [Overview & Prerequisites](#overview--prerequisites)
2. [Step 1: Create Datasets & Set Permissions](#step-1-create-datasets--set-permissions)
3. [Step 2: Deployment on TrueNAS Scale](#step-2-deployment-on-truenas-scale)
   - [Method A: Electric Eel (24.10+) Native Docker Compose (Recommended)](#method-a-electric-eel-2410-native-docker-compose-recommended)
   - [Method B: Dragonfish (24.04) Custom App](#method-b-dragonfish-2404-custom-app)
4. [Step 3: Reverse Proxy & WebSocket Configuration](#step-3-reverse-proxy--websocket-configuration)
   - [Nginx Proxy Manager on TrueNAS](#nginx-proxy-manager-on-truenas)
   - [Traefik / Caddy on TrueNAS](#traefik--caddy-on-truenas)
   - [Cloudflare Tunnel](#cloudflare-tunnel)
5. [Step 4: Automated ZFS Snapshots & Backups](#step-4-automated-zfs-snapshots--backups)
6. [Updating LocalRoast on TrueNAS](#updating-localroast-on-truenas)
7. [Troubleshooting & FAQs](#troubleshooting--faqs)

---

## Overview & Prerequisites

LocalRoast runs as a self-contained web service that includes:

- The ASP.NET Core 10 backend API and OpenAPI documentation.
- The Angular interactive web application served from static files.
- Microsoft SQL Server 2022 for relational database storage.
- A persistent local storage directory (`/data/uploads`) for recipe cover photos, thumbnails, step images, and document attachments.

### Prerequisites

- A running **TrueNAS Scale** instance.
- An initialized storage pool (e.g. `tank`, `ssd`, or `pool0`).
- **Apps** service configured and running on your preferred storage pool (**Apps** -> **Settings** -> **Choose Pool**).
- A domain name or local DNS record pointing to your TrueNAS host (e.g. `localroast.myhomelab.net` or `192.168.1.100`).

---

## Step 1: Create Datasets & Set Permissions

LocalRoast requires two persistent directories on your ZFS pool:
1. **`db`**: Microsoft SQL Server data files (`.mdf`, `.ldf`). SQL Server runs inside its container as UID `10001`.
2. **`uploads`**: Recipe cover photos, thumbnails, and PDF/text attachments saved by the ASP.NET Core backend into `/data/uploads`.

### 1. Create the AppData Datasets

1. In the TrueNAS Scale WebUI, navigate to **Datasets**.
2. Select your main storage pool (e.g. `tank` or `ssd/apps`) and click **Add Dataset**.
3. Configure the parent dataset:
   - **Name**: `localroast`
   - **Dataset Preset**: `Generic`
4. Click **Save**.
5. Select the newly created `localroast` dataset, click **Add Dataset**, and create the database child dataset:
   - **Name**: `db`
   - **Path**: `/mnt/tank/localroast/db` (or `/mnt/ssd/apps/localroast/db`)
6. Click **Save**.
7. Select `localroast` again, click **Add Dataset**, and create the media uploads child dataset:
   - **Name**: `uploads`
   - **Path**: `/mnt/tank/localroast/uploads` (or `/mnt/ssd/apps/localroast/uploads`)
8. Click **Save**.

### 2. Configure Permissions

Open the TrueNAS Scale web shell or an SSH session to your TrueNAS host:

```bash
# 1. Set ownership and permissions for SQL Server (UID 10001)
sudo chown -R 10001:0 /mnt/tank/localroast/db
sudo chmod -R 770 /mnt/tank/localroast/db

# 2. Set permissions for uploaded media (accessible by container and SMB shares)
sudo chmod -R 775 /mnt/tank/localroast/uploads
```

> [!NOTE]
> Adjust `/mnt/tank/localroast` to match your actual ZFS pool and dataset path (e.g. `/mnt/ssd/apps/localroast`).

---

## Step 2: Deployment on TrueNAS Scale

### Method A: Electric Eel (24.10+) Native Docker Compose (Recommended)

TrueNAS Scale 24.10 (Electric Eel) natively supports Docker Compose via **Custom App**.

1. In the TrueNAS Scale WebUI, navigate to **Apps**.
2. Click **Discover Apps** in the top-right corner.
3. In the top-right search/menu area, click the three-dots menu or **Custom App** / **Install via Docker Compose**.
4. Set the **Application Name**: `localroast`.
5. Under **Compose File**, paste the following configuration:

```yaml
name: local-roast-prod

services:
  api:
    image: ghcr.io/tylersmart/localroast:latest
    container_name: local-roast-api
    restart: unless-stopped
    ports:
      # Randomized host port (e.g. 62781) chosen per project so multiple apps on TrueNAS never collide
      - "62781:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_HTTP_PORTS: 8080
      ConnectionStrings__DefaultConnection: "Server=sqlserver;Database=LocalRoast;User Id=sa;Password=YourStrongPassword123!_MustIncludeCaps;TrustServerCertificate=True;"

      # JWT Authentication Secret
      Authentication__Jwt__Secret: "ReplaceWithAStrongSecretKeyAtLeast32CharactersLong!"
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
      # Host bind-mount to your ZFS dataset for uploaded recipe photos, thumbnails, and attachments
      - /mnt/tank/localroast/uploads:/data/uploads
    depends_on:
      sqlserver:
        condition: service_healthy

  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: local-roast-sqlserver
    restart: unless-stopped
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "YourStrongPassword123!_MustIncludeCaps"
      MSSQL_PID: "Developer"
    volumes:
      # Host bind-mount to your ZFS dataset for SQL Server relational data
      - /mnt/tank/localroast/db:/var/opt/mssql
    healthcheck:
      test:
        [
          "CMD-SHELL",
          "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrongPassword123!_MustIncludeCaps' -C -Q 'SELECT 1' -b -o /dev/null",
        ]
      interval: 5s
      timeout: 5s
      retries: 24
      start_period: 20s
```

> [!IMPORTANT]
> - Replace `YourStrongPassword123!_MustIncludeCaps` with your own secure database password.
> - Replace `ReplaceWithAStrongSecretKeyAtLeast32CharactersLong!` with a 32+ character random string (e.g. generated via `openssl rand -base64 32`).
> - Update `/mnt/tank/localroast/db` and `/mnt/tank/localroast/uploads` to your actual dataset paths (e.g. `/mnt/ssd/apps/localroast/...`).
> - Because both volumes are host bind-mounts directly to your ZFS datasets, no top-level `volumes:` block is required.

6. Click **Save** / **Install**.
7. Wait for the status indicator to transition from `Deploying` to `Running`.

---

### Method B: Dragonfish (24.04) Custom App

For TrueNAS Scale 24.04 (Dragonfish), deploy using the Custom App wizard:

1. In the WebUI, go to **Apps** -> **Discover Apps** -> **Custom App**.
2. **Application Name**: `localroast`.
3. **Container Image**:
   - Repository: `ghcr.io/tylersmart/localroast`
   - Tag: `latest`
4. **Port Forwarding**:
   - Container Port: `8080`
   - Node Port / Host Port: `62781` (or `30080` if using NodePort ranges)
5. **Environment Variables**:
   - `ASPNETCORE_ENVIRONMENT`: `Production`
   - `ASPNETCORE_HTTP_PORTS`: `8080`
   - `ConnectionStrings__DefaultConnection`: `Server=sqlserver;Database=LocalRoast;User Id=sa;Password=...`
   - `Authentication__Jwt__Secret`: `Your32CharKeyHere`
6. **Storage**:
   - **Volume 1 (Database)**:
     - Mount Type: `Host Path`
     - Host Path: `/mnt/tank/localroast/db`
     - Mount Path: `/var/opt/mssql`
   - **Volume 2 (Media Uploads)**:
     - Mount Type: `Host Path`
     - Host Path: `/mnt/tank/localroast/uploads`
     - Mount Path: `/data/uploads`
7. Click **Save** to deploy.

---

## Step 3: Reverse Proxy & WebSocket Configuration

To access LocalRoast with a friendly URL (e.g. `https://localroast.yourdomain.com`) and secure SSL encryption, route external traffic through a reverse proxy.

### Nginx Proxy Manager on TrueNAS

If you run **Nginx Proxy Manager** (available from the TrueNAS community catalog):

1. Log into Nginx Proxy Manager.
2. Go to **Proxy Hosts** -> **Add Proxy Host**.
3. **Details Tab**:
   - Domain Names: `localroast.yourdomain.com`
   - Scheme: `http`
   - Forward Hostname / IP: IP address of your TrueNAS server (e.g. `192.168.1.100`)
   - Forward Port: `62781`
   - Enable: **Cache Assets**, **Block Common Exploits**, **Websockets Support** (Essential for SignalR live co-shopping synchronization!)
4. **SSL Tab**:
   - SSL Certificate: Request a new Let's Encrypt Certificate or choose your wildcard certificate.
   - Enable: **Force SSL**, **HTTP/2 Support**.
5. Click **Save**.

---

### Traefik / Caddy on TrueNAS

Ensure your reverse proxy enables WebSocket upgrades. In Caddy, WebSockets are proxied automatically:

```caddy
localroast.yourdomain.com {
    reverse_proxy 192.168.1.100:62781
}
```

---

### Cloudflare Tunnel

If using a Cloudflare Tunnel:

1. In Cloudflare Zero Trust, add a public hostname:
   - Subdomain: `localroast`
   - Domain: `yourdomain.com`
   - Service: `HTTP://<truenas-internal-ip>:62781`
2. Under **Additional application settings**:
   - Ensure **WebSockets** is toggled ON.

---

## Step 4: Automated ZFS Snapshots & Backups

One of the greatest benefits of running on TrueNAS Scale is atomic, zero-downtime backups using ZFS snapshots.

### Configure Periodic Snapshots

1. In the TrueNAS Scale WebUI, navigate to **Data Protection** -> **Periodic Snapshot Tasks**.
2. Click **Add**.
3. **Dataset**: `tank/localroast`.
4. **Recursive**: Checked (this ensures both `db` and `uploads` child datasets are snapshotted simultaneously).
5. **Schedule**:
   - Frequency: Daily (e.g. at `02:00 AM`).
   - Lifetime: 14 Days (or your retention preference).
6. Click **Save**.

Now, if a recipe is accidentally deleted, or if data corruption occurs, you can roll back both the database and media files instantly to any previous day's snapshot without losing disk space.

---

## Updating LocalRoast on TrueNAS

1. In the TrueNAS Scale WebUI, navigate to **Apps**.
2. Locate the `localroast` app.
3. If an update is detected, an **Upgrade** badge will appear. Click **Upgrade**.
4. Or, in the app's three-dot menu, select **Restart** / **Redeploy** to pull the latest image.
5. On container start, the application checks for pending database migrations and applies them automatically.
6. Check the container logs in the WebUI (**Apps** -> **localroast** -> **Logs**) to confirm:
   ```text
   Database migration complete.
   Now listening on: http://[::]:8080
   ```

---

## Troubleshooting & FAQs

### 1. Database fails to start with "Permission Denied"

- **Cause**: SQL Server running as UID 10001 cannot write to the ZFS host dataset.
- **Solution**: Open the TrueNAS shell and run:
  ```bash
  sudo chown -R 10001:0 /mnt/tank/localroast/db
  sudo chmod -R 770 /mnt/tank/localroast/db
  ```
  Then restart the app.

### 2. Recipe photos, thumbnails, or attachments disappear or return 404 after update

- **Cause**: The `api` service was deployed without a volume mount for `/data/uploads`. In Docker, any file written to an unmounted path is stored in the container's ephemeral overlay layer and destroyed whenever the container is updated or recreated.
- **Solution**: Add the host path volume mount under `services.api.volumes`:
  ```yaml
  volumes:
    - /mnt/tank/localroast/uploads:/data/uploads
  ```
  And verify permissions on the host dataset:
  ```bash
  sudo chmod -R 775 /mnt/tank/localroast/uploads
  ```

### 3. Collaborators cannot see real-time shopping list updates

- **Cause**: The reverse proxy or Cloudflare tunnel is blocking WebSocket connections.
- **Solution**: Ensure **WebSockets Support** is enabled in Nginx Proxy Manager or that your reverse proxy passes `Upgrade: websocket` and `Connection: Upgrade` headers for the `/api/hubs/shopping` SignalR endpoint.

### 4. Port conflict with TrueNAS WebUI

- **Cause**: TrueNAS uses port 80/443 for its management portal, and many custom apps claim 8080.
- **Solution**: LocalRoast maps host port `62781` by default to avoid conflicts.
  ```yaml
  ports:
    - "62781:8080"
  ```
  Access the web interface at `http://truenas-ip:62781`.
