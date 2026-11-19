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
