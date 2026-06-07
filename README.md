# HelloDockerMCP Deployment and Usage Guide

English | [简体中文](./README_CN.md)

## Table of Contents

- [1. Overview](#1-overview)
- [Part 1: Deployment](#part-1-deployment)
  - [2. Prerequisites](#2-prerequisites)
  - [3. Clone the Repository](#3-clone-the-repository)
  - [4. Configure the Image Allowlist](#4-configure-the-image-allowlist)
  - [5. Confirm the storage Workspace](#5-confirm-the-storage-workspace)
  - [6. Configure Environment Variables](#6-configure-environment-variables)
  - [7. Public Access and Port Assumptions](#7-public-access-and-port-assumptions)
  - [8. frpc Configuration](#8-frpc-configuration)
  - [9. Start the Services](#9-start-the-services)
  - [10. Deployment Summary](#10-deployment-summary)
- [Part 2: storage Workspace Guide](#part-2-storage-workspace-guide)
  - [11. storage Workspace Location](#11-storage-workspace-location)
  - [12. Available storage Operations](#12-available-storage-operations)
  - [13. Recommended Usage](#13-recommended-usage)
  - [14. Sensitive File Warning](#14-sensitive-file-warning)
- [Part 3: Testing](#part-3-testing)
  - [15. GPT MCP Connection Test](#15-gpt-mcp-connection-test)
  - [16. Add the MCP Service in GPT](#16-add-the-mcp-service-in-gpt)
  - [17. Complete OAuth Authorization](#17-complete-oauth-authorization)
  - [18. Ask GPT to List Available Tools](#18-ask-gpt-to-list-available-tools)
  - [19. Ask GPT to Run a hello world Command](#19-ask-gpt-to-run-a-hello-world-command)
  - [20. Expected Result](#20-expected-result)
  - [21. Acceptance Criteria](#21-acceptance-criteria)

## 1. Overview

HelloDockerMCP is an MCP service started with Docker Compose. It allows GPT to call Docker-related capabilities through MCP, such as creating containers, running commands, testing HTTP access, and working with the repository-level `storage` workspace.

The Compose stack contains the following services:

| Component | Container name | Purpose |
|---|---|---|
| MCP service | `hello-docker-mcp` | Exposes the MCP HTTP service |
| OAuth service | `hello-docker-auth` | Provides authorization for MCP access |
| Docker-in-Docker | `hello-docker-dind` | Provides the Docker runtime used by the MCP service |
| frpc | `frpc4hello-docker-mcp` | Optional component |

This guide assumes the following public URLs:

| Service | Public URL |
|---|---|
| MCP service | `https://hellodockermcp.example.com` |
| OAuth service | `https://hellodockerauth.example.com` |

---

# Part 1: Deployment

## 2. Prerequisites

Install the following tools on the deployment machine:

```bash
docker
docker compose
git
```

Confirm that Docker Compose is available:

```bash
docker compose version
```

## 3. Clone the Repository

```bash
git clone https://github.com/BoysheO/HelloDockerMCP.git
cd HelloDockerMCP
```

## 4. Configure the Image Allowlist

Before starting the containers, review and configure the Docker image allowlist. This prevents GPT calls from failing because the requested image is not allowed by the MCP server.

Configuration file:

```text
HelloDockerMcp/appsettings.json
```

The `Docker.AllowedImages` field controls which images MCP is allowed to create or run.

Default example:

```json
"AllowedImages": [
  "nginx",
  "redis",
  "python",
  "alpine",
  "hello-world",
  "ubuntu",
  "mcr.microsoft.com/dotnet/sdk"
]
```

If you want GPT to use additional images, add them to this list. Example:

```json
"AllowedImages": [
  "nginx",
  "redis",
  "python",
  "alpine",
  "hello-world",
  "ubuntu",
  "mcr.microsoft.com/dotnet/sdk",
  "node",
  "busybox"
]
```

### 4.1 Pre-pull Frequently Used Images

It is recommended to pre-pull frequently used images, especially large images.

For images used by the Compose stack, pull them on the deployment host before startup if needed:

```bash
docker pull docker:27-dind
docker pull mcr.microsoft.com/dotnet/sdk:10.0
```

For images that GPT will use through the MCP Docker runtime, the images need to be available in the Docker-in-Docker environment. After the stack has started, you may pre-pull common images inside `hello-docker-dind`:

```bash
docker exec hello-docker-dind docker pull hello-world
docker exec hello-docker-dind docker pull alpine
docker exec hello-docker-dind docker pull ubuntu
docker exec hello-docker-dind docker pull python
```

Reason:

- If the target image has not been pulled yet, Docker will download it during the GPT-triggered MCP call.
- Large image downloads may take a long time.
- If the pull takes too long, the GPT-to-MCP API call may time out.
- Pre-pulling common images reduces first-call failures and improves response time.

Notes:

- Only allow images that are actually needed.
- Avoid opening the allowlist too broadly.
- Avoid allowing images from unknown sources.
- Save the allowlist configuration before container deployment.
- If the service is already running, restart the service after modifying the allowlist.

## 5. Confirm the storage Workspace

The repository already provides a `storage` directory. It also contains a `txt` file that can be used for file-access testing after deployment.

Host path:

```text
HelloDockerMCP/storage
```

Repository-relative path:

```text
./storage
```

Container path:

```text
/storage
```

On Linux, confirm the directory permissions if needed:

```bash
chmod 777 storage
```

Do not put the only copy of important files in `storage`.

## 6. Configure Environment Variables

Create a `.env` file in the repository root:

```env
HELLO_DOCKER_OAUTH_ISSUER=https://hellodockerauth.example.com
HELLO_DOCKER_OAUTH_SIGNING_KEY=please-change-this-key-at-least-32-bytes
HELLO_DOCKER_OAUTH_SCOPE=mcp
HELLO_DOCKER_OAUTH_RESOURCE_NAME=Hello Docker MCP

HELLO_DOCKER_OAUTH_ACCESS_TOKEN_LIFETIME_SECONDS=3600
HELLO_DOCKER_OAUTH_AUTHORIZATION_CODE_LIFETIME_SECONDS=300

HELLO_DOCKER_OAUTH_USERNAME=admin
HELLO_DOCKER_OAUTH_PASSWORD=change-this-password

HELLO_DOCKER_PASSKEYS_ENABLED=true
HELLO_DOCKER_PASSKEYS_RELYING_PARTY_NAME=Hello Docker MCP
HELLO_DOCKER_PASSKEYS_RELYING_PARTY_ID=hellodockerauth.example.com
HELLO_DOCKER_PASSKEYS_REQUIRE_USER_VERIFICATION=false
```

For production deployment, change at least the following values:

```env
HELLO_DOCKER_OAUTH_SIGNING_KEY
HELLO_DOCKER_OAUTH_PASSWORD
```

| Variable | Description |
|---|---|
| `HELLO_DOCKER_OAUTH_ISSUER` | Public URL of the OAuth service |
| `HELLO_DOCKER_OAUTH_SIGNING_KEY` | OAuth token signing key |
| `HELLO_DOCKER_OAUTH_SCOPE` | OAuth scope, default: `mcp` |
| `HELLO_DOCKER_OAUTH_RESOURCE_NAME` | MCP resource name |
| `HELLO_DOCKER_OAUTH_USERNAME` | OAuth login username |
| `HELLO_DOCKER_OAUTH_PASSWORD` | OAuth login password |
| `HELLO_DOCKER_PASSKEYS_ENABLED` | Enables passkey registration and sign-in |
| `HELLO_DOCKER_PASSKEYS_RELYING_PARTY_NAME` | Display name shown by the browser during passkey prompts |
| `HELLO_DOCKER_PASSKEYS_RELYING_PARTY_ID` | Public host name of the OAuth service, without scheme |
| `HELLO_DOCKER_PASSKEYS_REQUIRE_USER_VERIFICATION` | Requires authenticator user verification when set to `true` |

## 7. Public Access and Port Assumptions

This guide assumes that the Compose configuration exposes the MCP and Auth services to the public access path.

Recommended port mapping:

```yaml
hello-docker-mcp:
  ports:
    - "5000:5000"

hello-docker-auth:
  ports:
    - "5001:5001"
```

Recommended reverse proxy mapping:

| Domain | Upstream target |
|---|---|
| `https://hellodockermcp.example.com` | `http://SERVER_IP:5000` |
| `https://hellodockerauth.example.com` | `http://SERVER_IP:5001` |

## 8. frpc Configuration

If you do not use frpc, or if you do not know what frpc is, comment out or remove the `frpc` service section in `docker-compose.yml`.

Section to comment out or remove:

```yaml
frpc:
  image: ghcr.io/snowdreamtech/frpc:0.63.0
  container_name: frpc4hello-docker-mcp
  volumes:
    - ./frpc.toml:/etc/frp/frpc.toml:ro
  depends_on:
    hello-docker-mcp:
      condition: service_healthy
    hello-docker-auth:
      condition: service_healthy
  restart: always
```

## 9. Start the Services

Run the following command in the repository root:

```bash
docker compose up -d
```

The first startup will pull images and start the following services:

```text
hello-docker-mcp
hello-docker-auth
hello-docker-dind
```

If `frpc` is not commented out, it will also start:

```text
frpc4hello-docker-mcp
```

### 9.1 First Startup Waiting Notice

The first use of `hello-docker-dind` may take longer because the Docker-in-Docker environment needs to initialize.

The following steps may take time:

- Pulling the `docker:27-dind` image.
- Initializing the Docker daemon inside DinD.
- Pulling application images inside DinD during the first MCP call.
- Creating and starting containers for the first time.

The first startup or first GPT-triggered Docker call may be significantly slower than later usage. Wait for the initialization to complete.

To reduce timeout risk during the first GPT call, pre-pull common images as described in [4.1 Pre-pull Frequently Used Images](#41-pre-pull-frequently-used-images).

## 10. Deployment Summary

```bash
git clone https://github.com/BoysheO/HelloDockerMCP.git
cd HelloDockerMCP

# 1. Configure image allowlist
# Edit Docker.AllowedImages in HelloDockerMcp/appsettings.json

# 2. Confirm the storage directory
# The repository already provides ./storage
chmod 777 storage

# 3. Create .env
cat > .env <<'EOF'
HELLO_DOCKER_OAUTH_ISSUER=https://hellodockerauth.example.com
HELLO_DOCKER_OAUTH_SIGNING_KEY=please-change-this-key-at-least-32-bytes
HELLO_DOCKER_OAUTH_SCOPE=mcp
HELLO_DOCKER_OAUTH_RESOURCE_NAME=Hello Docker MCP
HELLO_DOCKER_OAUTH_ACCESS_TOKEN_LIFETIME_SECONDS=3600
HELLO_DOCKER_OAUTH_AUTHORIZATION_CODE_LIFETIME_SECONDS=300
HELLO_DOCKER_OAUTH_REFRESH_TOKEN_LIFETIME_SECONDS=2592000
HELLO_DOCKER_OAUTH_USERNAME=admin
HELLO_DOCKER_OAUTH_PASSWORD=change-this-password
HELLO_DOCKER_PASSKEYS_ENABLED=true
HELLO_DOCKER_PASSKEYS_RELYING_PARTY_NAME=Hello Docker MCP
HELLO_DOCKER_PASSKEYS_RELYING_PARTY_ID=hellodockerauth.example.com
HELLO_DOCKER_PASSKEYS_REQUIRE_USER_VERIFICATION=false
EOF

# 4. If you do not use frpc, or do not know what frpc is,
# comment out the frpc service in docker-compose.yml

# 5. Start services
docker compose up -d

# 6. Optional: pre-pull common images inside DinD after startup
docker exec hello-docker-dind docker pull hello-world
docker exec hello-docker-dind docker pull alpine
```

---

# Part 2: storage Workspace Guide

## 11. storage Workspace Location

`storage` is the workspace where GPT can operate files through MCP.

Host path:

```text
HelloDockerMCP/storage
```

Repository-relative path:

```text
./storage
```

Container path:

```text
/storage
```

The repository already provides this directory, and it contains a `txt` file that can be used for file-access testing after deployment.

## 12. Available storage Operations

GPT can use MCP tools to perform the following operations in this directory:

```text
read
create
upload
move
copy
delete
git clone
git fetch
git pull
git status
git branch list
git checkout
```

Treat this directory as a workspace that GPT is allowed to operate.

Git operations are restricted to this same storage workspace. Repository paths, clone destinations, and optional private key paths are always interpreted as paths under `storage`.

For SSH repositories, GitMCP uses the MCP service runtime's default OpenSSH identity when no `privateKeyPath` is provided. In Docker Compose deployments, mount host SSH config and keys into the MCP container if you want that default identity to use the host private keys:

```yaml
volumes:
  - ~/.ssh:/root/.ssh:ro
```

GPT may also use a private key file placed under `storage` by passing that file as `privateKeyPath`. Do not place production-only secrets in `storage`; use task-scoped or disposable deploy keys when possible.

## 13. Recommended Usage

For better performance, prepare files directly on the host machine and place them into the repository-level `storage` directory before asking GPT to operate on them.

Recommended workflow:

```text
Prepare files on the host machine
Copy them into HelloDockerMCP/storage
Ask GPT to read or process them through MCP
```

This usually provides a better performance experience than asking GPT to upload files through MCP, especially for large files or many files.

## 14. Sensitive File Warning

GPT has safety protection mechanisms. If sensitive files, credentials, private data, or high-risk content are placed in `storage`, GPT may be blocked by safety protections when reading, writing, or processing these files.

Recommended content for `storage`:

```text
temporary files
test files
project files that can be processed publicly
files that GPT is allowed to modify or delete
```

Not recommended for `storage`:

```text
the only copy of important files
production secrets
account passwords
tokens
private certificate keys
personal private data
business-sensitive data
```

---

# Part 3: Testing

## 15. GPT MCP Connection Test

Test objective:

> Connect GPT to `https://hellodockermcp.example.com`, then ask GPT to call Docker through MCP and run a `hello world` command.

## 16. Add the MCP Service in GPT

Add the MCP service in GPT's MCP configuration:

```json
{
  "name": "HelloDockerMCP",
  "url": "https://hellodockermcp.example.com",
  "authorization": {
    "type": "oauth"
  }
}
```

Use the following authorization service URL:

```text
https://hellodockerauth.example.com
```

Use the credentials configured in `.env`:

```text
Username: admin
Password: change-this-password
```

## 17. Complete OAuth Authorization

When GPT connects to the MCP service, it should redirect to the OAuth authorization page.

Sign in with the credentials configured in `.env`, or register a passkey first and then use **Sign in with passkey**:

```text
admin / change-this-password
```

After authorization, GPT should show that the MCP service is connected.

## 18. Ask GPT to List Available Tools

Ask GPT:

```text
Please list the currently available MCP tools from HelloDockerMCP.
```

Expected result:

GPT should recognize Docker-related tools and `storage` file operation tools.

## 19. Ask GPT to Run a hello world Command

Ask GPT:

```text
Please use HelloDockerMCP to create and run a temporary container, execute a hello world command, return the output, and remove the container after completion.
```

Or more explicitly:

```text
Please use HelloDockerMCP to call Docker, run a container that executes:
echo "hello world"
Then return the execution result to me.
```

## 20. Expected Result

GPT should return a result similar to:

```text
hello world
```

If GPT returns `hello world`, the following checks have passed:

| Check | Result |
|---|---|
| GPT connected to MCP | Passed |
| OAuth authorization | Passed |
| MCP service is reachable | Passed |
| MCP can connect to Docker-in-Docker | Passed |
| Docker command execution | Passed |

## 21. Acceptance Criteria

Deployment is considered successful when all of the following are true:

1. GPT can add the MCP service:

```text
https://hellodockermcp.example.com
```

2. GPT can complete OAuth authorization:

```text
https://hellodockerauth.example.com
```

3. GPT can list HelloDockerMCP tools.

4. GPT can call Docker through MCP and execute:

```bash
echo "hello world"
```

5. GPT returns:

```text
hello world
```
