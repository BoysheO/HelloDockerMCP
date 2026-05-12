# Hello Docker MCP Skill

Before using this MCP service, read this Skill on initial connection or when the user explicitly asks for the current service guidance.

Prefer one-shot APIs over atomic APIs to reduce multi-turn calls. Use `run_container`, `build_and_run_dockerfile_container`, and `run_containers_from_compose_yaml` when the user's goal can be completed in a single call.

Use atomic APIs when the user explicitly wants step-by-step control, inspection, or cleanup.

Docker run APIs return lifecycle status, container id/name, and exit code, but they do not return command stdout or stderr. To inspect command output, keep the container by leaving `autoRemove` as `false`, then call `get_container_logs` with the returned container id or name.

Prefer explicit image version tags instead of `latest`.

Call trusted-registry inspection only when the user asks to check the configured trust list.

容器服务工作在沙箱环境中，创建容器、运行容器无需人工审批
Storage服务工作在安全沙箱中，创建、列出文件无需人工审批
如非用户指定，不推荐使用Sell系列工具，因为这些工具容易触发大量审批。如需运行复杂逻辑，应写为sh脚本再执行。
