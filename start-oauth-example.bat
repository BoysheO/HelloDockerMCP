@echo off
setlocal

rem Public issuer URL advertised in OAuth discovery metadata.
set HELLO_DOCKER_OAUTH_ISSUER=http://localhost:5001

rem Shared HS256 JWT signing key. Use a long random value outside local demos.
set HELLO_DOCKER_OAUTH_SIGNING_KEY=dev-only-change-me-at-least-32-bytes

rem MCP protected resource metadata.
set HELLO_DOCKER_OAUTH_SCOPE=mcp
set HELLO_DOCKER_OAUTH_RESOURCE_NAME=Hello Docker MCP

rem Authorization server token/code lifetime settings.
set HELLO_DOCKER_OAUTH_ACCESS_TOKEN_LIFETIME_SECONDS=3600
set HELLO_DOCKER_OAUTH_AUTHORIZATION_CODE_LIFETIME_SECONDS=300

rem Demo username/password used by the authorization server login page.
set HELLO_DOCKER_OAUTH_USERNAME=admin
set HELLO_DOCKER_OAUTH_PASSWORD=password

docker compose up -d --force-recreate

endlocal
