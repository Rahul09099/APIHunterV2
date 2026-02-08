# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
WORKDIR /src

# Copy project files for restore
COPY UnsecuredAPIKeys.WebAPI/*.csproj ./UnsecuredAPIKeys.WebAPI/
COPY UnsecuredAPIKeys.Data/*.csproj ./UnsecuredAPIKeys.Data/
COPY UnsecuredAPIKeys.Services/*.csproj ./UnsecuredAPIKeys.Services/
COPY UnsecuredAPIKeys.Providers/*.csproj ./UnsecuredAPIKeys.Providers/

# Restore Web API project (restores all dependencies)
RUN dotnet restore UnsecuredAPIKeys.WebAPI/UnsecuredAPIKeys.WebAPI.csproj

# Copy remaining source code
COPY . .

# Publish the Web API
WORKDIR /src/UnsecuredAPIKeys.WebAPI
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install SQLite (needed for database operations)
RUN apt-get update && apt-get install -y sqlite3 libsqlite3-dev && rm -rf /var/lib/apt/lists/*

# Copy published files
COPY --from=publish /app/publish .

# Create directory for database
RUN mkdir -p /app/data

# Set environment variables
ENV ASPNETCORE_URLS=http://+:$PORT
ENV DATABASE_PATH=/app/data/unsecuredapikeys.db

# Expose port (Render assigns this dynamically)
EXPOSE $PORT

ENTRYPOINT ["dotnet", "UnsecuredAPIKeys.WebAPI.dll"]
