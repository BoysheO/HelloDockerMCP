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
set HELLO_DOCKER_OAUTH_REFRESH_TOKEN_LIFETIME_SECONDS=2592000

rem Demo username/password used by the authorization server login page.
set HELLO_DOCKER_OAUTH_USERNAME=admin
set HELLO_DOCKER_OAUTH_PASSWORD=password

rem Passkey/WebAuthn settings. Localhost is allowed for browser passkeys during local demos.
set HELLO_DOCKER_PASSKEYS_ENABLED=true
set HELLO_DOCKER_PASSKEYS_RELYING_PARTY_NAME=Hello Docker MCP
set HELLO_DOCKER_PASSKEYS_RELYING_PARTY_ID=localhost
set HELLO_DOCKER_PASSKEYS_REQUIRE_USER_VERIFICATION=false

docker compose up -d --force-recreate

endlocal
