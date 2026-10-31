# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ServerMonitor.sln ./
COPY src/ServerMonitor.Domain/ServerMonitor.Domain.csproj src/ServerMonitor.Domain/
COPY src/ServerMonitor.Application/ServerMonitor.Application.csproj src/ServerMonitor.Application/
COPY src/ServerMonitor.Infrastructure/ServerMonitor.Infrastructure.csproj src/ServerMonitor.Infrastructure/
COPY src/ServerMonitor.Web/ServerMonitor.Web.csproj src/ServerMonitor.Web/
RUN dotnet restore src/ServerMonitor.Web/ServerMonitor.Web.csproj
COPY src/ src/
RUN dotnet publish src/ServerMonitor.Web/ServerMonitor.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# iputils-ping: fallback for the ICMP probe; curl: container health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends iputils-ping curl tzdata \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=root:root /app/publish .
# The key ring is the only path the app writes to; everything else stays root-owned and read-only.
RUN mkdir -p /app/keys && chown app:app /app/keys && chmod 700 /app/keys
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeysPath=/app/keys \
    Https__Redirect=false \
    DOTNET_gcServer=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
VOLUME ["/app/keys"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "ServerMonitor.Web.dll"]
