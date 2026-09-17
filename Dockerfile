FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/ESbirka.Mcp/ESbirka.Mcp.csproj \
    -c Release --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir /data && chown "$APP_UID:$APP_UID" /data
COPY --from=build --chown=$APP_UID:$APP_UID /app .
ENV ESbirka__CachePath=/data/cache.db \
    Mcp__HttpUrl=http://0.0.0.0:3001 \
    DOTNET_EnableDiagnostics=0
EXPOSE 3001
VOLUME ["/data"]
USER $APP_UID
ENTRYPOINT ["dotnet", "ESbirka.Mcp.dll", "http"]