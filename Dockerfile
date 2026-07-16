# ---- Etapa de compilación ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restaurar dependencias (capa cacheable)
COPY idempotencia.csproj ./
RUN dotnet restore idempotencia.csproj

# Compilar y publicar
COPY . ./
RUN dotnet publish idempotencia.csproj -c Release -o /app/publish --no-restore

# ---- Etapa de ejecución ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Instalar curl para el healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Usuario no root (la imagen base trae "app" predefinido, UID 64198)
USER app

# Puerto de escucha dentro del contenedor
EXPOSE 5000
ENV ASPNETCORE_HTTP_PORTS=5000 \
    ASPNETCORE_ENVIRONMENT=Production

COPY --from=build --chown=app:app /app/publish ./

HEALTHCHECK --interval=10s --timeout=5s --retries=5 --start-period=30s \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "idempotencia.dll"]