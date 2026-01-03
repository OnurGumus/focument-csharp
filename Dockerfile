# Build Stage
FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /App

# Copy solution and project files first for better caching
COPY *.sln ./
COPY src/Model/Model.csproj src/Model/
COPY src/Server/Server.csproj src/Server/

# Restore dependencies
RUN dotnet restore

# Copy all source code
COPY src/ src/

# Build and publish
RUN dotnet build -c Release && \
    dotnet publish src/Server/Server.csproj -c Release -o deploy

# Verify the publish output
RUN ls -la /App/deploy

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /App

# Create non-root user for security (handle case where GID/UID 1000 already exists)
RUN getent group 1000 || groupadd --gid 1000 appgroup && \
    id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home appuser && \
    mkdir -p /home/appuser && chown -R 1000:1000 /home/appuser

# Copy the published output from the build stage
COPY --from=build-env /App/deploy .

# Set permissions
RUN chown -R 1000:1000 /App

# Switch to non-root user
USER 1000

# Expose the necessary port
EXPOSE 8080

# Set the entry point
ENTRYPOINT ["dotnet", "Server.dll"]
