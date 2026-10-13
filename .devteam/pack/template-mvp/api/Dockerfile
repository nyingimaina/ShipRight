FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Api.csproj src/
RUN dotnet restore src/Api.csproj
COPY src/ src/
RUN dotnet publish src/Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# Where the app keeps its data files. Mount a volume here so they survive the container (see docker-compose.yml).
RUN mkdir /data && chown $APP_UID /data
ENV ASPNETCORE_URLS=http://+:8080
ENV Data__Directory=/data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MyApp.Api.dll"]
