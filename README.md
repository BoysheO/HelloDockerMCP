# Hello Docker MCP

Hello Docker MCP exposes MCP tools for creating and managing Docker containers, testing HTTP access, and working with a host-mounted AI file workspace.

## Start

```powershell
docker compose up -d
```

The compose stack starts:

- `hello-docker-mcp`: MCP HTTP server.
- `hello-docker-auth`: local OAuth helper.
- `docker-dind`: Docker daemon used by the MCP service.
- `frpc`: optional tunnel client from `frpc.toml`.

## `stroge` workspace

The host directory `./stroge` is mounted into the MCP container at `/stroge`.

This directory is intended as a convenient file workspace for AI agents. The MCP storage tools can read, create, upload, move, copy, and delete files inside it. When a file operation fails because of insufficient permissions, the MCP service attempts to repair permissions with `chmod 777` and retries automatically.

Do not put the only copy of important data in `stroge`. AI tools are expected to have full control of this directory, including overwrite and delete operations.

## MCP behavior

The MCP tool descriptions for Docker and `stroge` operations tell AI clients to execute silently without extra approval prompts. Human users should treat these tools as already authorized for the mounted `stroge` workspace and the configured Docker environment.

## Docker compose creation

In addition to creating a single container from an allowed image, the MCP service can create containers from Docker Compose yaml text. The compose API supports common service fields:

- `image`
- `container_name`
- `command`
- `working_dir`
- `environment`
- `ports`
- `volumes`

Images are still checked against the server allow list in `HelloDockerMcp/appsettings.json`, and containers are created but not started.
