# AT Resource Graph AppView + Reader sample

This repository is a small, deterministic .NET 10 vertical slice:

```text
checked-in bundle/record/feed fixtures
              |
              v
      fixture importer + projector
              |
              v
        SQLite read model
              |
              v
  AppView HTTP/XRPC-style query
              |
              v
       separate Reader client
```

The sample consumes the reusable `lqdev/at-resource-graph` primitives. It does
not copy or reimplement the Core graph model, AT record envelope decoder,
syndication parser, Bluesky adapter, or draft `IResourceGraphAppView` contract.

## Quickstart

Prerequisites: .NET SDK `10.0.401` or a compatible .NET 10 feature band and a
GitHub token with `read:packages` access to the `lqdev/at-resource-graph`
packages.

```powershell
git clone https://github.com/lqdev/at-resource-graph-appview.git
cd at-resource-graph-appview
$env:GITHUB_TOKEN = gh auth token
dotnet nuget update source github --username lqdev --password $env:GITHUB_TOKEN --store-password-in-clear-text --configfile NuGet.config
dotnet restore AtResourceGraph.AppView.sln
dotnet build AtResourceGraph.AppView.sln --no-restore
dotnet run --project src\AtResourceGraph.Seed -- fixtures data\appview.db
```

In separate terminals:

```powershell
$env:AppView__DatabasePath = "$(Get-Location)\data\appview.db"
dotnet run --project src\AtResourceGraph.AppView.Api --urls http://localhost:5070
```

```powershell
$env:APPVIEW_API_BASE_URL = "http://localhost:5070/"
dotnet run --project src\AtResourceGraph.Reader --urls http://localhost:5071
```

Open <http://localhost:5071>. The Reader calls only
`GET /xrpc/me.lqdev.resourcegraph.appview.getBundle?bundle=...`; it does not
open SQLite or fetch a feed/PDS URL. The API also exposes `/healthz` and
`/readyz`.

The API is safe to start offline with an empty database. Optional startup
seeding is enabled only when `AppView__SeedFixtures=true`; the fixture
directory can be overridden with `AppView__FixtureDirectory`.

## Boundaries and dependency matrix

| Boundary | Project | Responsibility |
| --- | --- | --- |
| Transport contract | `AtResourceGraph.AppView.Contracts` | App-specific JSON envelope and typed HTTP client |
| Application/storage | `AtResourceGraph.AppView.Application` | Fixture ingestion, validation, projection, SQLite repository, primitive AppView adapter |
| HTTP host | `AtResourceGraph.AppView.Api` | Health/readiness and draft XRPC-style query |
| Reader | `AtResourceGraph.Reader` | Separate HTTP consumer and HTML rendering |
| Seed CLI | `AtResourceGraph.Seed` | Offline fixture import |
| Tests | `AtResourceGraph.AppView.Tests` | Schema/projection, replay/update/delete, API, Reader-to-AppView, and end-to-end checks |

| Dependency | Version/source |
| --- | --- |
| .NET | `10.0.401` SDK, `net10.0` |
| `Microsoft.Data.Sqlite` | `10.0.12` |
| xUnit | `2.9.3` |
| ASP.NET Core testing | `10.0.12` |
| ResourceGraph primitives | GitHub Packages `ResourceGraph.Core`, `ResourceGraph.AppView`, `ResourceGraph.AtProto`, `ResourceGraph.Bluesky`, and `ResourceGraph.Syndication`, exact version `0.1.0-preview.1` |

The primitives are consumed as immutable exact-version packages from
`https://nuget.pkg.github.com/lqdev/index.json`. `NuGet.config` maps only
`ResourceGraph.*` packages to GitHub Packages and keeps framework/test
dependencies on nuget.org. CI grants the job least-privilege `packages: read`
and injects `GITHUB_TOKEN` into a transient source credential. The package
owner must grant this repository read access to the GitHub Packages packages;
where cross-repository `GITHUB_TOKEN` access is not available, configure a
repository `read:packages` PAT secret and substitute it in the workflow.
Local restore uses the same source command shown above; credentials are never
committed.
The sample contracts and storage schema remain app-specific.

## Fixtures and projection behavior

`fixtures/events.jsonl` contains a create, an exact replay, and an update for
one bundle. `fixtures/delete-event.json` exercises deletion. The bundle has
one RSS reference and one AT record reference. `fixtures/records/bluesky-post.json`
is decoded by `ResourceGraph.AtProto` and projected by `ResourceGraph.Bluesky`;
`fixtures/feeds/reading.xml` is parsed by `ResourceGraph.Syndication`.

The SQLite schema is initialized deterministically and stores:

- bundles, ordered memberships, reference identity/kind/URI/CID;
- source record JSON, CID, and fixture revision;
- projected item/feed fields and metadata;
- projection version, indexed timestamp, and diagnostics;
- replay-safe event IDs and an ingestion cursor.

All SQL values are parameterized. A duplicate event ID is a no-op; a new event
with an older cursor is rejected. Updates replace the ordered membership
projection, and deletes make the bundle unavailable to the read API without
performing a network request.

## Direct public reads versus an indexed AppView

A direct public-read adapter resolves a current public resource on demand. An
AppView instead answers from a previously validated and indexed read model. This
sample demonstrates the latter: request handling never fetches arbitrary PDS,
feed, or web URLs. The fixture projector is the offline stand-in for an
ingestion pipeline and preserves source CID/revision, indexed time, and
diagnostics in the response envelope.

The Lexicon under `lexicons/me/lqdev/resourcegraph/appview/` is a draft
incubation contract only. It is not a stable community standard.

## Validation

```powershell
dotnet restore AtResourceGraph.AppView.sln
dotnet build AtResourceGraph.AppView.sln --no-restore
dotnet test AtResourceGraph.AppView.sln --no-restore
```

The tests use temporary SQLite files and checked-in fixtures only. They do not
require network access.

## Security and non-goals

This sample intentionally excludes full-network backfill, OAuth writes,
production PDS deployment, moderation, media processing, arbitrary URL
fetching, HTML scraping, and Tap/firehose ingestion. Fixture content is
synthetic and contains no credentials or personal data. The HTTP hosts set
basic browser hardening headers, bound the Reader's API timeout, and expose
only the fixed read surface.

## Roadmap

1. Add an optional Tap-backed event adapter that feeds the same validated
   projector and cursor repository; keep fixture mode as the deterministic
   offline test path.
2. Add operational metrics, retention, and deployment-specific authentication
   without widening the draft query into a write or arbitrary-fetch API.
