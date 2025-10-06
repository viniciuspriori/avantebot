# ================================
# Build Stage
# ================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy and restore dependencies
COPY ["AvanteBot.API/AvanteBot.API.csproj", "AvanteBot.API/"]
RUN dotnet restore "AvanteBot.API/AvanteBot.API.csproj"

# Copy the rest of the files and build
COPY . .
WORKDIR "/src/AvanteBot.API"
RUN dotnet publish "AvanteBot.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# ================================
# Runtime Stage
# ================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Copy published output
COPY --from=build /app/publish .

# ✅ Important for Render (uses dynamic port)
ENV ASPNETCORE_URLS=http://+:${PORT}

# Optional: explicitly expose a default (Render ignores it, but good practice)
EXPOSE 10000

# Start the app
ENTRYPOINT ["dotnet", "AvanteBot.API.dll"]
