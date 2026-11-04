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
# Where the dashboard's Grafana links point. Compose passes the local stack's GRAFANA_ROOT_URL; the default is
# the public Grafana that the production image links to.
ARG VITE_GRAFANA_URL=https://grafana.tickpulse.dev
RUN npm run build

# ---------- build: restore once, publish both apps ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
# Hand-maintained: one line per project under src/ that Api or Cli references. A new project needs a line here,
# otherwise the restore below misses it and the --no-restore publish fails.
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
# No Kerberos/GSSAPI libraries (krb5-libs): TickPulse forces Npgsql's GSS encryption off (NpgsqlConnectionPolicy), so
# nothing loads libgssapi_krb5, and the native GSS negotiation that crashed this musl image under load never runs.
WORKDIR /app
ENV DOTNET_NOLOGO=1
# COPY leaves the files owned by root, so the process (the image's unprivileged "app" user) cannot modify them.
# The base image already listens on 8080 (ASPNETCORE_HTTP_PORTS).
COPY --from=build /out/api ./
COPY --from=web /web/dist ./wwwroot
USER app
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD wget -qO- http://localhost:8080/health/live >/dev/null || exit 1
ENTRYPOINT ["dotnet", "TickPulse.Api.dll"]

# ---------- cli: the console client ----------
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS cli
WORKDIR /app
ENV DOTNET_NOLOGO=1
COPY --from=build /out/cli ./
USER app
ENTRYPOINT ["dotnet", "TickPulse.Cli.dll"]
