FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ArcadeNode.Node.csproj ./
RUN dotnet restore
COPY *.cs ./
RUN dotnet publish --no-restore -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
EXPOSE 8443
USER $APP_UID
ENTRYPOINT ["dotnet", "ArcadeNode.Node.dll"]
