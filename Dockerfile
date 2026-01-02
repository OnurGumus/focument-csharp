# Build Stage
FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /App

# Copy solution and config files first
COPY *.sln ./
COPY .config/ .config/

# Copy dependency files
COPY paket.lock paket.dependencies ./
COPY paket-files/ paket-files/

# Install dependencies in a single layer - these are cached unless dependencies change
RUN dotnet tool restore && \
    dotnet paket restore

# Copy all source projects
COPY src/ src/

# Build and publish
RUN dotnet restore && \
    dotnet build -c Release && \
    dotnet publish src/Server/Server.csproj -c Release -o deploy

# Verify the publish output
RUN ls -la /App/deploy

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /App

# Create non-root user for security (handle case where GID/UID 1000 already exists)
RUN getent group 1000 || groupadd --gid 1000 appgroup && \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser && \
    # Ensure the user has a home directory regardless of how it was created
    mkdir -p /home/appuser && chown -R 1000:1000 /home/appuser

# Copy the published output from the build stage
COPY --from=build-env /App/deploy .

# Set permissions
RUN chown -R 1000:1000 /App

# Switch to non-root user (use UID for compatibility)
USER 1000

# Expose the necessary port
EXPOSE 8080

# Set the entry point for the application
ENTRYPOINT ["dotnet", "Server.dll"]
