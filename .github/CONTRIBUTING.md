# Contributing

Keep the sample deterministic and offline. Changes should preserve the
application/primitives boundary, use parameterized SQLite commands, and add or
update focused tests for ingestion, projection, API contracts, and the
Reader-to-AppView HTTP path.

Run `dotnet restore AtResourceGraph.AppView.sln`, `dotnet build
AtResourceGraph.AppView.sln --no-restore`, and `dotnet test
AtResourceGraph.AppView.sln --no-restore` before opening a pull request.
