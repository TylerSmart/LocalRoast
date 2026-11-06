# OIDC & OAuth2 Authentication Setup Guide

This guide explains how to configure external OpenID Connect (OIDC) and OAuth 2.0 authentication providers (such as **Google**, **Authentik**, **Keycloak**, or **Microsoft Entra ID**) for LocalRoast when deploying with Docker on a home server, VPS, or cloud host.

---

## Table of Contents

- [OIDC \& OAuth2 Authentication Setup Guide](#oidc--oauth2-authentication-setup-guide)
  - [Table of Contents](#table-of-contents)
  - [How OIDC Authentication Works in LocalRoast](#how-oidc-authentication-works-in-localroast)
  - [Step-by-Step Guide: Google OAuth 2.0](#step-by-step-guide-google-oauth-20)
    - [Prerequisites](#prerequisites)
    - [Step 1: Create a Google Cloud Project](#step-1-create-a-google-cloud-project)
    - [Step 2: Configure the OAuth Consent Screen](#step-2-configure-the-oauth-consent-screen)
    - [Step 3: Create OAuth 2.0 Client Credentials](#step-3-create-oauth-20-client-credentials)
  - [Deploying with Docker](#deploying-with-docker)
    - [Environment Variables Reference](#environment-variables-reference)
    - [Sample `docker-compose.yml`](#sample-docker-composeyml)
    - [Sample `.env` File](#sample-env-file)
  - [Reverse Proxy \& HTTPS Setup](#reverse-proxy--https-setup)
    - [Caddy Example:](#caddy-example)
    - [Nginx Example:](#nginx-example)
  - [Other Common OIDC Providers](#other-common-oidc-providers)
    - [Authentik](#authentik)
    - [Keycloak](#keycloak)
    - [Microsoft Entra ID (Azure AD)](#microsoft-entra-id-azure-ad)
  - [Troubleshooting \& FAQs](#troubleshooting--faqs)
    - [Error: `redirect_uri_mismatch`](#error-redirect_uri_mismatch)
    - [User profile missing name or email](#user-profile-missing-name-or-email)
    - [Can I still use local accounts when OIDC is enabled?](#can-i-still-use-local-accounts-when-oidc-is-enabled)

---

## How OIDC Authentication Works in LocalRoast

LocalRoast supports both local email/password registration and external OIDC identity providers.

When OIDC is enabled:
1. The frontend queries `/api/auth/providers` to check available sign-in options.
2. Clicking **"Sign in with [Provider]"** redirects the user to your provider's authorization page.
3. Upon authentication, the provider redirects back to `/login/callback` with an authorization code.
4. LocalRoast securely exchanges the code with the provider for an ID token, retrieves the user's profile (`email`, `name`, `sub`), and:
   - **Links existing accounts** if a local account already exists with the matching email address.
   - **Auto-provisions a new user** if no account exists yet.
5. Issues a secure JWT session token for LocalRoast.

---

## Step-by-Step Guide: Google OAuth 2.0

### Prerequisites
- A Google account.
- Your public domain name or local address where LocalRoast is hosted (e.g. `https://diagrams.yourdomain.com` or `http://localhost:62781`).

---

### Step 1: Create a Google Cloud Project

1. Navigate to the [Google Cloud Console](https://console.cloud.google.com/).
2. Click the project dropdown at the top of the page and select **New Project**.
3. Name your project (e.g., `LocalRoast`) and click **Create**.
4. Make sure your newly created project is selected in the top bar.

---

### Step 2: Configure the OAuth Consent Screen

1. In the left navigation menu, go to **APIs & Services** > **OAuth consent screen**.
2. Select the user type:
   - **External**: Anyone with a Google account can sign in (recommended for home servers / public access).
   - **Internal**: Only users within your Google Workspace organization can sign in.
3. Click **Create**.
4. Fill in the required application details:
   - **App name**: `LocalRoast`
   - **User support email**: Your email address
   - **Developer contact information**: Your email address
5. Click **Save and Continue**.
6. On the **Scopes** page:
   - Click **Add or Remove Scopes**.
   - Select the following standard scopes:
     - `.../auth/userinfo.email`
     - `.../auth/userinfo.profile`
     - `openid`
   - Click **Update**, then **Save and Continue**.
7. If your app is in "Testing" status, add your own email under **Test users** (or publish the app to Production under the OAuth consent screen dashboard so any user can sign in).
8. Click **Save and Continue** to finish.

---

### Step 3: Create OAuth 2.0 Client Credentials

1. In the left menu, select **APIs & Services** > **Credentials**.
2. Click **+ Create Credentials** at the top and choose **OAuth client ID**.
3. Under **Application type**, choose **Web application**.
4. Set a name (e.g., `LocalRoast Web Client`).
5. Under **Authorized JavaScript origins**, click **+ Add URI** and enter your instance URL (without trailing slash):
   ```text
   https://diagrams.yourdomain.com
   ```
   *(For local testing: `http://localhost:62781`)*
6. Under **Authorized redirect URIs**, click **+ Add URI** and enter your callback URL:
   ```text
   https://diagrams.yourdomain.com/login/callback
   ```
   *(For local testing: `http://localhost:62781/login/callback`)*
7. Click **Create**.
8. A modal will display your **Client ID** and **Client Secret**. Copy both values for the next step.

---

## Deploying with Docker

### Environment Variables Reference

In ASP.NET Core, nested configuration properties are specified in environment variables using double underscores (`__`):

| Environment Variable | Description | Value for Google |
|----------------------|-------------|------------------|
| `Authentication__Oidc__Enabled` | Enables external OIDC authentication | `true` |
| `Authentication__Oidc__ProviderName` | Display name shown on the login button | `Google` |
| `Authentication__Oidc__Authority` | HTTPS OIDC issuer / authority URL used for discovery and signing keys | `https://accounts.google.com` |
| `Authentication__Oidc__ClientId` | OAuth Client ID and expected ID-token audience | `xxxx.apps.googleusercontent.com` |
| `Authentication__Oidc__ClientSecret` | OAuth Client Secret from Google Cloud Console | `GOCSPX-xxxx` |
| `Authentication__Oidc__Scope` | Requested OIDC scopes (space-delimited) | `openid profile email` |
| `Authentication__Oidc__AuthorizationEndpoint` | Provider authorization URL | `https://accounts.google.com/o/oauth2/v2/auth` |
| `Authentication__Oidc__TokenEndpoint` | Provider token exchange URL | `https://oauth2.googleapis.com/token` |
| `Authentication__Oidc__UserInfoEndpoint` | Provider user info URL | `https://openidconnect.googleapis.com/v1/userinfo` |
| `Authentication__Jwt__Secret` | Cryptographic secret for LocalRoast JWTs | *At least 32 random characters* |

---

### Sample `docker-compose.yml`

Here is an example `docker-compose.yml` configuration configured for Google OIDC:

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
      ASPNETCORE_HTTP_PORTS: "8080"
      ConnectionStrings__DefaultConnection: "Server=sqlserver;Database=LocalRoast;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True;"

      # JWT Session Secret (Must be at least 32 characters)
      Authentication__Jwt__Secret: "${JWT_SECRET}"
      Authentication__Jwt__Issuer: "LocalRoast"
      Authentication__Jwt__Audience: "LocalRoast"
      Authentication__Jwt__ExpirationMinutes: 1440

      # Google OIDC Configuration
      Authentication__Oidc__Enabled: "true"
      Authentication__Oidc__ProviderName: "Google"
      Authentication__Oidc__Authority: "https://accounts.google.com"
      Authentication__Oidc__ClientId: "${GOOGLE_CLIENT_ID}"
      Authentication__Oidc__ClientSecret: "${GOOGLE_CLIENT_SECRET}"
      Authentication__Oidc__Scope: "openid profile email"
      Authentication__Oidc__AuthorizationEndpoint: "https://accounts.google.com/o/oauth2/v2/auth"
      Authentication__Oidc__TokenEndpoint: "https://oauth2.googleapis.com/token"
      Authentication__Oidc__UserInfoEndpoint: "https://openidconnect.googleapis.com/v1/userinfo"
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
      MSSQL_PID: "Developer"
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
```

---

### Sample `.env` File

Place a `.env` file alongside `docker-compose.yml`:

```env
# Database password
MSSQL_SA_PASSWORD=YourStrongDatabasePassword123!_MustIncludeCapsAndSpecial

# LocalRoast JWT signing key (generate a random 32+ character string)
JWT_SECRET=ReplaceWithAStrongSecretKeyAtLeast32CharactersLong!

# Randomized Host Port (assigned per-app so multiple apps don't collide on the same server)
APP_PORT=62781

# Google OAuth Credentials
GOOGLE_CLIENT_ID=your-google-client-id.apps.googleusercontent.com
GOOGLE_CLIENT_SECRET=GOCSPX-your-google-client-secret
```

Launch the stack:
```bash
docker compose up -d
```

---

## Reverse Proxy & HTTPS Setup

Most external identity providers (including Google) require HTTPS for redirect URIs outside of `localhost`.

When placing LocalRoast behind a reverse proxy (such as **Nginx**, **Caddy**, **Traefik**, or **Nginx Proxy Manager**), ensure the proxy forwards the standard proxy headers:

- `X-Forwarded-For`
- `X-Forwarded-Proto` (`https`)
- `X-Forwarded-Host`

### Caddy Example:
```caddy
diagrams.yourdomain.com {
    reverse_proxy localhost:62781
}
```

### Nginx Example:
```nginx
server {
    listen 443 ssl http2;
    server_name diagrams.yourdomain.com;

    # SSL configuration...

    location / {
        proxy_pass http://localhost:62781;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

---

## Other Common OIDC Providers

### Authentik
- **Authority**: `https://authentik.yourdomain.com/application/o/localroast/`
- **Authorization Endpoint**: `https://authentik.yourdomain.com/application/o/authorize/`
- **Token Endpoint**: `https://authentik.yourdomain.com/application/o/token/`
- **UserInfo Endpoint**: `https://authentik.yourdomain.com/application/o/userinfo/`
- **Scope**: `openid profile email`

### Keycloak
- **Authority**: `https://keycloak.yourdomain.com/realms/your-realm`
- **Authorization Endpoint**: `https://keycloak.yourdomain.com/realms/your-realm/protocol/openid-connect/auth`
- **Token Endpoint**: `https://keycloak.yourdomain.com/realms/your-realm/protocol/openid-connect/token`
- **UserInfo Endpoint**: `https://keycloak.yourdomain.com/realms/your-realm/protocol/openid-connect/userinfo`
- **Scope**: `openid profile email`

### Microsoft Entra ID (Azure AD)
- **Authority**: `https://login.microsoftonline.com/{tenant-id}/v2.0`
- **Authorization Endpoint**: `https://login.microsoftonline.com/{tenant-id}/oauth2/v2.0/authorize`
- **Token Endpoint**: `https://login.microsoftonline.com/{tenant-id}/oauth2/v2.0/token`
- **UserInfo Endpoint**: `https://graph.microsoft.com/oidc/userinfo`
- **Scope**: `openid profile email`

---

## Troubleshooting & FAQs

### Error: `redirect_uri_mismatch`
- **Cause**: The redirect URI registered in your identity provider console does not match the URI sent during login.
- **Fix**: Check that the redirect URI in your provider console exactly matches `https://your-domain.com/login/callback` (including protocol `https` and without trailing slash).

### User profile missing name or email
- **Cause**: The configured `Scope` does not include `email` and `profile`.
- **Fix**: Ensure `Authentication__Oidc__Scope` contains `openid profile email`.

### Can I still use local accounts when OIDC is enabled?
- **Yes**: Local email/password registration and authentication remain fully supported alongside OIDC. If an existing local user logs in with a Google account using the exact same email address, LocalRoast automatically links the Google identity to their existing account.
