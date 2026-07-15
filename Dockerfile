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

# Puerto de escucha dentro del contenedor
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "idempotencia.dll"]
