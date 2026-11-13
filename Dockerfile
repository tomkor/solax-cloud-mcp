# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /build

# Copy the app project only (secrets and tests are excluded via .dockerignore)
COPY src/SolaxCloudMcp/ ./
RUN dotnet publish -c Release -o /app

# Runtime stage (multi-arch image: x64 and ARM64, e.g. Raspberry Pi 64-bit)
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

COPY --from=build /app ./

# Inside the container listen on all interfaces; exposure is controlled by the port mapping
ENV HTTP_HOST=0.0.0.0

# The unprivileged user that the .NET images provide
USER $APP_UID

# Health check for HTTP mode (the image has no curl)
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD ["dotnet", "SolaxCloudMcp.dll", "healthcheck"]

# Default to HTTP mode; override TRANSPORT to use MCP (stdio) mode
ENV TRANSPORT=http

EXPOSE 8000

# Run the MCP/HTTP server
CMD ["dotnet", "SolaxCloudMcp.dll"]
