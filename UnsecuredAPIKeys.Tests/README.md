# Test and Release Commands

Run these commands from the repository root in PowerShell. Package versions are pinned in the project files; restore once, then use `--no-restore` for repeatable local gates.

```powershell
dotnet restore .\UnsecuredAPIKeys-OpenSource.sln
```

## Task 1.2 targeted WebAPI and SQLite smoke tests

```powershell
dotnet test .\UnsecuredAPIKeys.Tests\UnsecuredAPIKeys.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~UnsecuredAPIKeys.Tests.WebApiHostSmokeTests"
```

The targeted class starts the real WebAPI request pipeline, reaches an MVC controller, and verifies that startup creates the EF Core model schema in a GUID-named shared-memory SQLite database. PostgreSQL test infrastructure is intentionally deferred to task 9.1, where its first concurrency tests are introduced.

## Complete test project

```powershell
dotnet test .\UnsecuredAPIKeys.Tests\UnsecuredAPIKeys.Tests.csproj -c Release --no-restore
```

## Release solution build

The required release-gate command is:

```powershell
dotnet build .\UnsecuredAPIKeys-OpenSource.sln -c Release
```

After the explicit restore above, the equivalent no-network repeat is:

```powershell
dotnet build .\UnsecuredAPIKeys-OpenSource.sln -c Release --no-restore
```
