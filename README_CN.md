# HelloDockerMCP 使用与部署流程说明书

[English](./README.md) | 简体中文

## 目录

- [1. 项目概述](#1-项目概述)
- [第一部分：部署](#第一部分部署)
  - [2. 部署前准备](#2-部署前准备)
  - [3. 拉取项目代码](#3-拉取项目代码)
  - [4. 配置镜像白名单](#4-配置镜像白名单)
  - [5. 确认 storage 工作目录](#5-确认-storage-工作目录)
  - [6. 配置环境变量](#6-配置环境变量)
  - [7. 端口与公网访问假定](#7-端口与公网访问假定)
  - [8. frpc 配置](#8-frpc-配置)
  - [9. 启动服务](#9-启动服务)
  - [10. 部署流程总览](#10-部署流程总览)
- [第二部分：storage 工作区使用说明](#第二部分storage-工作区使用说明)
  - [11. storage 工作区定位](#11-storage-工作区定位)
  - [12. storage 可执行操作](#12-storage-可执行操作)
  - [13. 推荐使用方式](#13-推荐使用方式)
  - [14. 敏感文件风险提示](#14-敏感文件风险提示)
- [第三部分：测试](#第三部分测试)
  - [15. GPT 接入 MCP 测试流程](#15-gpt-接入-mcp-测试流程)
  - [16. 在 GPT 中添加 MCP 服务](#16-在-gpt-中添加-mcp-服务)
  - [17. 完成 OAuth 授权](#17-完成-oauth-授权)
  - [18. 让 GPT 查看可用工具](#18-让-gpt-查看可用工具)
  - [19. 让 GPT 执行 hello world 测试](#19-让-gpt-执行-hello-world-测试)
  - [20. 预期测试结果](#20-预期测试结果)
  - [21. 验收标准](#21-验收标准)

## 1. 项目概述

HelloDockerMCP 是一个基于 Docker Compose 启动的 MCP 服务，用于让 GPT 通过 MCP 调用 Docker 相关能力，例如创建容器、执行命令、测试 HTTP 访问，以及操作代码库中的 `storage` 工作目录。

服务栈包含：

| 组件 | 容器名 | 作用 |
|---|---|---|
| MCP 服务 | `hello-docker-mcp` | 对外提供 MCP HTTP 服务 |
| OAuth 服务 | `hello-docker-auth` | 提供 MCP 访问授权 |
| Docker-in-Docker | `hello-docker-dind` | 为 MCP 服务提供 Docker 执行环境 |
| frpc | `frpc4hello-docker-mcp` | 可选组件 |

本说明假定最终公网访问地址为：

| 服务 | 公网地址 |
|---|---|
| MCP 服务 | `https://hellodockermcp.example.com` |
| OAuth 服务 | `https://hellodockerauth.example.com` |

---

# 第一部分：部署

## 2. 部署前准备

部署机器需要提前安装：

```bash
docker
docker compose
git
```

确认 Docker Compose 可用：

```bash
docker compose version
```

## 3. 拉取项目代码

```bash
git clone https://github.com/BoysheO/HelloDockerMCP.git
cd HelloDockerMCP
```

## 4. 配置镜像白名单

在启动容器前，建议先检查并配置 Docker 镜像白名单，避免 GPT 调用 MCP 时因镜像不在允许范围内而执行失败。

配置文件路径：

```text
HelloDockerMcp/appsettings.json
```

其中 `Docker.AllowedImages` 是允许 MCP 创建或运行的镜像列表。

当前默认配置示例：

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

如果后续希望 GPT 使用其他镜像，需要在这里追加镜像名。例如：

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

### 4.1 建议提前拉取常用镜像

建议提前拉取白名单中的常用镜像，尤其是体积较大的镜像。

Compose 服务本身使用的镜像，可根据需要在宿主机器上提前拉取：

```bash
docker pull docker:27-dind
docker pull mcr.microsoft.com/dotnet/sdk:10.0
```

GPT 通过 MCP 使用的业务镜像，需要在 Docker-in-Docker 环境中可用。服务启动后，可以在 `hello-docker-dind` 内提前拉取常用镜像：

```bash
docker exec hello-docker-dind docker pull hello-world
docker exec hello-docker-dind docker pull alpine
docker exec hello-docker-dind docker pull ubuntu
docker exec hello-docker-dind docker pull python
```

原因：

- GPT 调用 MCP 执行 Docker 任务时，如果目标镜像尚未拉取，Docker 会先执行镜像下载。
- 较大的镜像下载时间可能较长。
- 如果镜像拉取耗时过久，可能导致 GPT 调用 MCP API 超时失败。
- 提前拉取常用镜像可以降低首次调用失败概率，并改善响应体验。

注意事项：

- 只添加确实需要 GPT 使用的镜像。
- 不建议开放过多镜像。
- 不建议允许来源不明的镜像。
- 修改镜像白名单后，需要在容器部署前完成保存。
- 如果服务已经启动，修改后需要重启服务使配置生效。

## 5. 确认 storage 工作目录

代码库中已经提供了 `storage` 目录，并且该目录中已有一个 `txt` 文件，可用于部署成功后的文件访问类测试。

`storage` 的宿主机路径是：

```text
HelloDockerMCP/storage
```

也就是代码库根目录下的：

```text
./storage
```

容器内路径是：

```text
/storage
```

如果是在 Linux 环境部署，可确认目录权限：

```bash
chmod 777 storage
```

注意：不要将唯一副本的重要数据放入 `storage`。

## 6. 配置环境变量

在项目根目录创建 `.env` 文件：

```env
HELLO_DOCKER_OAUTH_ISSUER=https://hellodockerauth.example.com
HELLO_DOCKER_OAUTH_SIGNING_KEY=please-change-this-key-at-least-32-bytes
HELLO_DOCKER_OAUTH_SCOPE=mcp
HELLO_DOCKER_OAUTH_RESOURCE_NAME=Hello Docker MCP

HELLO_DOCKER_OAUTH_ACCESS_TOKEN_LIFETIME_SECONDS=3600
HELLO_DOCKER_OAUTH_AUTHORIZATION_CODE_LIFETIME_SECONDS=300

HELLO_DOCKER_OAUTH_USERNAME=admin
HELLO_DOCKER_OAUTH_PASSWORD=change-this-password
```

正式部署时必须修改：

```env
HELLO_DOCKER_OAUTH_SIGNING_KEY
HELLO_DOCKER_OAUTH_PASSWORD
```

其中：

| 配置项 | 说明 |
|---|---|
| `HELLO_DOCKER_OAUTH_ISSUER` | OAuth 服务的公网地址 |
| `HELLO_DOCKER_OAUTH_SIGNING_KEY` | OAuth Token 签名密钥 |
| `HELLO_DOCKER_OAUTH_SCOPE` | OAuth Scope，默认 `mcp` |
| `HELLO_DOCKER_OAUTH_RESOURCE_NAME` | MCP 资源名称 |
| `HELLO_DOCKER_OAUTH_USERNAME` | OAuth 登录用户名 |
| `HELLO_DOCKER_OAUTH_PASSWORD` | OAuth 登录密码 |

## 7. 端口与公网访问假定

本说明假定 Compose 中已经将 MCP 和 Auth 服务端口开放到公网访问链路中。

建议端口映射如下：

```yaml
hello-docker-mcp:
  ports:
    - "5000:5000"

hello-docker-auth:
  ports:
    - "5001:5001"
```

公网反向代理建议配置为：

| 域名 | 转发目标 |
|---|---|
| `https://hellodockermcp.example.com` | `http://服务器IP:5000` |
| `https://hellodockerauth.example.com` | `http://服务器IP:5001` |

## 8. frpc 配置

如果不使用 frpc，或不知道 frpc 是什么，则直接在 `docker-compose.yml` 中注释或删除 `frpc` 服务段即可。

需要注释或删除的部分大致如下：

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

## 9. 启动服务

在项目根目录执行：

```bash
docker compose up -d
```

首次启动时会拉取镜像并启动以下服务：

```text
hello-docker-mcp
hello-docker-auth
hello-docker-dind
```

如果未注释 `frpc`，还会启动：

```text
frpc4hello-docker-mcp
```

### 9.1 初次启动等待说明

初次使用 `hello-docker-dind` 时，Docker-in-Docker 环境需要完成初始化。

可能耗时较久的环节包括：

- 拉取 `docker:27-dind` 镜像。
- 初始化 DinD 内部 Docker Daemon。
- MCP 首次调用时在 DinD 内部拉取业务镜像。
- 首次创建和启动容器。

因此，第一次启动或第一次通过 GPT 调用 Docker 能力时，耗时可能明显长于后续使用。此时耐心等候即可。

如果要降低 GPT 首次调用超时概率，建议参考 [4.1 建议提前拉取常用镜像](#41-建议提前拉取常用镜像)。

## 10. 部署流程总览

```bash
git clone https://github.com/BoysheO/HelloDockerMCP.git
cd HelloDockerMCP

# 1. 配置镜像白名单
# 编辑 HelloDockerMcp/appsettings.json 中的 Docker.AllowedImages

# 2. 确认 storage 目录
# 代码库已提供 ./storage
chmod 777 storage

# 3. 创建 .env
cat > .env <<'EOF'
HELLO_DOCKER_OAUTH_ISSUER=https://hellodockerauth.example.com
HELLO_DOCKER_OAUTH_SIGNING_KEY=please-change-this-key-at-least-32-bytes
HELLO_DOCKER_OAUTH_SCOPE=mcp
HELLO_DOCKER_OAUTH_RESOURCE_NAME=Hello Docker MCP
HELLO_DOCKER_OAUTH_ACCESS_TOKEN_LIFETIME_SECONDS=3600
HELLO_DOCKER_OAUTH_AUTHORIZATION_CODE_LIFETIME_SECONDS=300
HELLO_DOCKER_OAUTH_USERNAME=admin
HELLO_DOCKER_OAUTH_PASSWORD=change-this-password
EOF

# 4. 如不使用 frpc，或不知道 frpc 是什么，编辑 docker-compose.yml 注释 frpc 服务

# 5. 启动服务
docker compose up -d

# 6. 可选：服务启动后，在 DinD 内提前拉取常用镜像
docker exec hello-docker-dind docker pull hello-world
docker exec hello-docker-dind docker pull alpine
```

---

# 第二部分：storage 工作区使用说明

## 11. storage 工作区定位

`storage` 是 GPT 通过 MCP 操作文件的工作目录。

宿主机路径：

```text
HelloDockerMCP/storage
```

代码库相对路径：

```text
./storage
```

容器内路径：

```text
/storage
```

代码库已经提供该目录，并且目录中已有一个 `txt` 文件，可供部署成功后进行文件访问类测试。

## 12. storage 可执行操作

GPT 可通过 MCP 工具对该目录执行：

```text
读取
创建
上传
移动
复制
删除
```

因此，该目录应被视为 GPT 可操作的工作区。

## 13. 推荐使用方式

建议尽可能在宿主机器上提前把需要 GPT 操作的文件准备到代码库中的 `storage` 目录内，而不是让 GPT 通过 MCP 上传文件。

推荐方式：

```text
在宿主机上准备文件
复制到 HelloDockerMCP/storage
让 GPT 通过 MCP 读取或处理这些文件
```

这样通常能获得更好的性能体验，尤其是处理较大文件或多个文件时。

## 14. 敏感文件风险提示

GPT 有安全防御机制。如果将敏感文件、密钥、凭据、隐私数据或高风险内容存放在 `storage` 中，GPT 对这些文件的读取、写入或处理可能会被安全防御机制阻止。

建议放入 `storage` 的内容：

```text
临时文件
测试文件
可公开处理的项目文件
允许 GPT 修改或删除的文件
```

不建议放入 `storage` 的内容：

```text
唯一副本的重要文件
生产环境密钥
账号密码
Token
证书私钥
个人隐私数据
商业敏感数据
```

---

# 第三部分：测试

## 15. GPT 接入 MCP 测试流程

测试目标：

> 让 GPT 成功连接 `https://hellodockermcp.example.com`，并通过 MCP 调用 Docker 能力执行一次 `hello world` 命令。

## 16. 在 GPT 中添加 MCP 服务

在 GPT 的 MCP 配置中添加服务：

```json
{
  "name": "HelloDockerMCP",
  "url": "https://hellodockermcp.example.com",
  "authorization": {
    "type": "oauth"
  }
}
```

授权服务地址使用：

```text
https://hellodockerauth.example.com
```

登录账号使用 `.env` 中配置的值：

```text
Username: admin
Password: change-this-password
```

## 17. 完成 OAuth 授权

在 GPT 接入 MCP 时，系统会跳转到 OAuth 授权页面。

使用 `.env` 中配置的账号密码登录：

```text
admin / change-this-password
```

授权完成后，GPT 应显示 MCP 服务已连接。

## 18. 让 GPT 查看可用工具

在 GPT 中输入：

```text
请列出 HelloDockerMCP 当前可用的 MCP 工具。
```

预期结果：

GPT 能识别到 Docker 相关工具和 `storage` 文件操作工具。

## 19. 让 GPT 执行 hello world 测试

在 GPT 中输入：

```text
请通过 HelloDockerMCP 创建并运行一个临时容器，执行 hello world 命令，输出完成后删除容器。
```

或更明确地输入：

```text
请使用 HelloDockerMCP 调用 Docker，运行一个容器执行：
echo "hello world"
并把执行结果返回给我。
```

## 20. 预期测试结果

GPT 最终应返回类似结果：

```text
hello world
```

如果 GPT 能成功返回 `hello world`，说明：

| 检查项 | 结果 |
|---|---|
| GPT 已接入 MCP | 成功 |
| OAuth 授权 | 成功 |
| MCP 服务可访问 | 成功 |
| MCP 可连接 Docker-in-Docker | 成功 |
| Docker 命令执行 | 成功 |

## 21. 验收标准

最终验收条件：

1. GPT 能成功添加 MCP 服务：

```text
https://hellodockermcp.example.com
```

2. GPT 能通过 OAuth 登录授权：

```text
https://hellodockerauth.example.com
```

3. GPT 能列出 HelloDockerMCP 工具。

4. GPT 能通过 MCP 调用 Docker 执行：

```bash
echo "hello world"
```

5. GPT 返回：

```text
hello world
```

满足以上条件，即认为 HelloDockerMCP 部署成功。
