# syntax=docker/dockerfile:1
#
# TickPulse images.
#   web    builds the React dashboard (static files)
#   build  restores and publishes TickPulse.Api and TickPulse.Cli
#   api    the API host: ASP.NET Core runtime + the dashboard in wwwroot      (docker build --target api .)
#   cli    the console client                                                 (docker build --target cli .)
#
# Dependency manifests are copied before the sources, so the npm ci and dotnet restore layers stay cached until
# package-lock.json, a csproj or a props file changes.

# ---------- web: the dashboard ----------
FROM node:24-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

# ---------- build: restore once, publish both apps ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/TickPulse.Domain/TickPulse.Domain.csproj                 src/TickPulse.Domain/
COPY src/TickPulse.Application/TickPulse.Application.csproj       src/TickPulse.Application/
COPY src/TickPulse.Infrastructure/TickPulse.Infrastructure.csproj src/TickPulse.Infrastructure/
COPY src/TickPulse.Contracts/TickPulse.Contracts.csproj           src/TickPulse.Contracts/
COPY src/TickPulse.Api/TickPulse.Api.csproj                       src/TickPulse.Api/
COPY src/TickPulse.Cli/TickPulse.Cli.csproj                       src/TickPulse.Cli/
RUN dotnet restore src/TickPulse.Api/TickPulse.Api.csproj \
 && dotnet restore src/TickPulse.Cli/TickPulse.Cli.csproj

COPY src/ src/
RUN dotnet publish src/TickPulse.Api/TickPulse.Api.csproj -c Release --no-restore -o /out/api -p:UseAppHost=false \
 && dotnet publish src/TickPulse.Cli/TickPulse.Cli.csproj -c Release --no-restore -o /out/cli -p:UseAppHost=false

# ---------- api: the deployable host ----------
# Alpine has no ICU, which is fine: every project sets InvariantGlobalization.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS api
# Npgsql probes for GSSAPI on every new connection; without the library each probe prints a load error to stderr.
RUN apk add --no-cache krb5-libs
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
COPY --from=build --chown=root:root /out/api ./
COPY --from=web   --chown=root:root /web/dist ./wwwroot
# The files stay owned by root and read-only to the process; it runs as the image's unprivileged "app" user.
USER app
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD wget -qO- http://localhost:8080/health/live >/dev/null || exit 1
ENTRYPOINT ["dotnet", "TickPulse.Api.dll"]

# ---------- cli: the console client ----------
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS cli
WORKDIR /app
ENV DOTNET_NOLOGO=1
COPY --from=build --chown=root:root /out/cli ./
USER app
ENTRYPOINT ["dotnet", "TickPulse.Cli.dll"]
