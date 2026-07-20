# Scoped test run

Run only the tests relevant to the current change — never the full solution suite (~3 min, CI covers it).

1. Identify which project the changed files belong to:
   - `src/BookmarkManager.Client/**` → `tests/BookmarkManager.Client.ComponentTests`
   - `src/BookmarkManager.Api/**` → `tests/BookmarkManager.Api.IntegrationTests` and/or `tests/BookmarkManager.UnitTests`
   - `BookmarkExtension/**` → `cd BookmarkExtension && npm test`
2. Run the narrowest scope:
   - Single project: `dotnet test tests/<Project>/<Project>.csproj`
   - Single class/method: `dotnet test --filter "FullyQualifiedName~Name"`
3. Report failures with the decisive error line, not the full log.
