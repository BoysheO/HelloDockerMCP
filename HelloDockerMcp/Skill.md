# Hello Docker MCP Skill

Before using this MCP service, read this Skill on initial connection or when the user explicitly asks for the current service guidance.

Prefer one-shot APIs over atomic APIs to reduce multi-turn calls. Use `run_container`, `build_and_run_dockerfile_container`, and `run_containers_from_compose_yaml` when the user's goal can be completed in a single call.

Use atomic APIs when the user explicitly wants step-by-step control, inspection, or cleanup.

Docker run APIs return lifecycle status, container id/name, and exit code, but they do not return command stdout or stderr. To inspect command output, keep the container by leaving `autoRemove` as `false`, then call `get_container_logs` with the returned container id or name.

Prefer explicit image version tags instead of `latest`.

Call trusted-registry inspection only when the user asks to check the configured trust list.

Git APIs operate only inside Storage. Use `clone_git_repository` for cloning repositories, then `get_git_status`, `pull_git_repository`, `fetch_git_repository`, `list_git_branches`, and `checkout_git_branch` for repository maintenance.

Git SSH defaults to the service runtime OpenSSH identity. In Docker Compose this means host keys must be mounted into the MCP container, such as `~/.ssh:/root/.ssh:ro`. If the user provides a private key file inside Storage, pass it as `privateKeyPath`; do not read or print the key content.

容器服务工作在沙箱环境中，创建容器、运行容器无需人工审批
Storage服务工作在安全沙箱中，创建、列出文件无需人工审批
Git服务工作在Storage安全边界内，克隆、状态查询、拉取和分支切换无需使用Shell系列工具。
如非用户指定，不应使用Sell系列工具，因为这些工具容易触发大量审批。并且会因为多次调用接口而导致MCPClient消耗更多Token。如需运行复杂逻辑，应写为sh脚本再执行。
写sh脚本的做法：编辑好脚本后，用storage接口送达到Storage内，然后在容器内执行此sh脚本即可。
