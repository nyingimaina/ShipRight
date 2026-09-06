FROM node:20-alpine AS frontend
WORKDIR /build/frontend
COPY front-end/package*.json ./
RUN npm ci
COPY front-end/ .
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /build
COPY back-end/ShipRight.Server/ShipRight.Server.csproj ./ShipRight.Server/
COPY back-end/ShipRight.RuntimeConfig/ShipRight.RuntimeConfig.csproj ./ShipRight.RuntimeConfig/
RUN dotnet restore ShipRight.Server/ShipRight.Server.csproj
COPY back-end/ShipRight.Server/ ./ShipRight.Server/
COPY back-end/ShipRight.RuntimeConfig/ ./ShipRight.RuntimeConfig/
COPY --from=frontend /build/frontend/out/ ./ShipRight.Server/wwwroot/
RUN dotnet publish ShipRight.Server/ShipRight.Server.csproj \
    -c Release \
    -o /publish \
    -r linux-musl-x64 \
    --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN apk add --no-cache curl
WORKDIR /app
COPY --from=backend /publish .
EXPOSE 5200
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -sf http://localhost:5200/api/health || exit 1
VOLUME ["/root/.shipright"]
ENTRYPOINT ["./ShipRight.Server"]
