# The backend image (D56): built in the .NET SDK image, run in Microsoft's chiseled ASP.NET image: no shell, no
# package manager, a non-root user by default, nothing to write to. arm64, like developer Macs and the cluster.

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS build
WORKDIR /src
# Restore first, from the project files only, so the package layer is reused when only code changes.
COPY Directory.Build.props Todo.slnx ./
COPY src/Core/Core.fsproj src/Core/
COPY src/TodoBackend/TodoBackend.fsproj src/TodoBackend/
RUN dotnet restore src/TodoBackend/TodoBackend.fsproj
COPY src/ src/
RUN dotnet publish src/TodoBackend/TodoBackend.fsproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled
WORKDIR /app
COPY --from=build /app ./
# What /version reports (BuildInfo): set by CI from the commit it builds.
ARG BUILD_COMMIT=local
ARG BUILD_TIME=local
ARG IMAGE_TAG=local
ENV BUILD_COMMIT=$BUILD_COMMIT BUILD_TIME=$BUILD_TIME IMAGE_TAG=$IMAGE_TAG
EXPOSE 8080
ENTRYPOINT ["dotnet", "TodoBackend.dll"]

# `dev`: what the local cluster runs (D61). The SDK with hot reload: the source is mounted read-only at /src, so
# packages are restored here, at build time, and `dotnet watch` runs offline (the network rules deny the
# internet). A change to the package list means rebuilding this image, which `make up` does.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS dev
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NUGET_PACKAGES=/nuget HOME=/tmp \
    DOTNET_USE_POLLING_FILE_WATCHER=1 DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=1 ASPNETCORE_HTTP_PORTS=8080 \
    # NuGet's vulnerability audit needs the internet; CI audits every build (and Trivy scans every image).
    NuGetAudit=false
WORKDIR /restore
COPY Directory.Build.props Todo.slnx ./
COPY src/Core/Core.fsproj src/Core/
COPY src/TodoBackend/TodoBackend.fsproj src/TodoBackend/
RUN HOME=/root dotnet restore src/TodoBackend/TodoBackend.fsproj && chmod -R a+rX /nuget
WORKDIR /src/backend
# Numeric, so Kubernetes can check runAsNonRoot.
USER 1654
ENTRYPOINT ["dotnet", "watch", "--non-interactive", "--project", "src/TodoBackend", "run", "--artifacts-path", "/tmp/artifacts"]

