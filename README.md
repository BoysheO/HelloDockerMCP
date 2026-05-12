# Hello Docker MCP

Hello Docker MCP exposes MCP tools for creating and managing Docker containers, testing HTTP access, and working with a host-mounted AI file workspace.

## Start

```powershell
docker compose up -d
```

Set `HELLO_DOCKER_OAUTH_ENABLED=false` when you need local development without MCP authentication.

The compose stack starts:

- `hello-docker-mcp`: MCP HTTP server.
- `hello-docker-auth`: local OAuth helper.
- `docker-dind`: Docker daemon used by the MCP service.
- `frpc`: optional tunnel client from `frpc.toml`.

## `storage` workspace

The host directory `./storage` is mounted into both the MCP container and the Docker-in-Docker daemon container at `/storage`.

This directory is intended as a convenient file workspace for AI agents. The MCP storage tools can read, create, upload, move, copy, and delete files inside it. When a file operation fails because of insufficient permissions, the MCP service attempts to repair permissions with `chmod 777` and retries automatically.

Do not put the only copy of important data in `storage`. AI tools are expected to have full control of this directory, including overwrite and delete operations.

## MCP behavior

The MCP tool descriptions for Docker and `storage` operations tell AI clients to execute silently without extra approval prompts. Human users should treat these tools as already authorized for the mounted `storage` workspace and the configured Docker environment.

Service logs are written to the console and to the local `./logs` directory.

## Docker compose creation

In addition to creating a single container from an allowed image, the MCP service can create containers from Docker Compose yaml text. The compose API supports common service fields:

- `image`
- `container_name`
- `command`
- `working_dir`
- `environment`
- `ports`
- `volumes`

Images are checked against trusted registry domains in `HelloDockerMcp/appsettings.json`. The default trusted registries are Docker Hub and `harbor.boysheo.com`.

Compose volume entries can mount `/storage` or a directory under `/storage`, for example `/storage:/work` or `/storage/project:/work`.

The service also exposes one-shot APIs for running Compose yaml, building and running a single-file Dockerfile, and managing local images.
