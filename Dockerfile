FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ProjectManagementWeb.slnx ./
COPY .config/dotnet-tools.json .config/dotnet-tools.json
COPY src/ProjectManagementWeb.Api/ProjectManagementWeb.Api.csproj src/ProjectManagementWeb.Api/
COPY src/ProjectManagementWeb.Application/ProjectManagementWeb.Application.csproj src/ProjectManagementWeb.Application/
COPY src/ProjectManagementWeb.Domain/ProjectManagementWeb.Domain.csproj src/ProjectManagementWeb.Domain/
COPY src/ProjectManagementWeb.Infrastructure/ProjectManagementWeb.Infrastructure.csproj src/ProjectManagementWeb.Infrastructure/
COPY tests/ProjectManagementWeb.UnitTests/ProjectManagementWeb.UnitTests.csproj tests/ProjectManagementWeb.UnitTests/
COPY tests/ProjectManagementWeb.IntegrationTests/ProjectManagementWeb.IntegrationTests.csproj tests/ProjectManagementWeb.IntegrationTests/
RUN dotnet restore ProjectManagementWeb.slnx
RUN dotnet tool restore
COPY . .
RUN dotnet build ProjectManagementWeb.slnx -c Release --no-restore
RUN dotnet publish src/ProjectManagementWeb.Api/ProjectManagementWeb.Api.csproj -c Release -o /app/publish --no-restore

FROM build AS migrator
ENTRYPOINT ["dotnet", "ef", "database", "update", "--project", "src/ProjectManagementWeb.Infrastructure", "--startup-project", "src/ProjectManagementWeb.Api", "--configuration", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/keys && chown -R $APP_UID:$APP_UID /app/keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ProjectManagementWeb.Api.dll"]
