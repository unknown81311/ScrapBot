FROM mcr.microsoft.com/dotnet/runtime:8.0-preview AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-preview AS build
WORKDIR /src
COPY ["ScrapBot/ScrapBot.csproj", "ScrapBot/"]
RUN dotnet restore "ScrapBot/ScrapBot.csproj"
COPY . .
WORKDIR "/src/ScrapBot"
RUN dotnet build "ScrapBot.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ScrapBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY webhooks.json /app/webhooks.json
ENTRYPOINT ["dotnet", "ScrapBot.dll"]
