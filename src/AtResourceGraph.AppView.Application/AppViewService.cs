using AtResourceGraph.AppView.Contracts;
using ResourceGraph.AppView;
using ResourceGraph.Core;

namespace AtResourceGraph.AppView.Application;

/// <summary>Adapts the persisted read model to the primitives AppView contract.</summary>
public sealed class AppViewService : IResourceGraphAppView
{
    private readonly ProjectionStore store;

    public AppViewService(ProjectionStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<Bundle?> GetPublicBundleAsync(
        string bundleIdentity,
        CancellationToken cancellationToken = default)
    {
        var projection = await store.ReadBundleAsync(bundleIdentity, cancellationToken);
        return projection is null ? null : ToCoreBundle(projection);
    }

    public async ValueTask<OperationResult<ExpandedGraphSnapshot>> ExpandPublicBundleAsync(
        string bundleIdentity,
        ExpansionLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var projection = await store.ReadBundleAsync(bundleIdentity, cancellationToken);
        if (projection is null)
        {
            return new OperationResult<ExpandedGraphSnapshot>(
                null,
                [
                    new GraphDiagnostic(
                        "appview.bundle.not_found",
                        $"No indexed bundle was found for '{bundleIdentity}'.",
                        DiagnosticSeverity.Error,
                        bundleIdentity)
                ]);
        }

        var resources = projection.Members
            .Take(limits.MaxExpandedItems)
            .Select(member =>
            {
                var resource = ToCoreReference(member);
                var provenance = new Provenance(
                    [new ProvenanceStep(projection.Identity, member.Position, resource.Identity)],
                    resource.Uri,
                    resource is AtRecordReference ? resource.Uri : null);
                return new ExpandedResource(resource, provenance);
            })
            .ToArray();
        var diagnostics = projection.Diagnostics.ToList();
        if (projection.Members.Count > limits.MaxExpandedItems)
        {
            diagnostics.Add(new GraphDiagnostic(
                "appview.expansion.output_limit",
                $"The persisted member count exceeds the requested limit of {limits.MaxExpandedItems}.",
                DiagnosticSeverity.Error,
                projection.Identity));
        }

        return new OperationResult<ExpandedGraphSnapshot>(
            new ExpandedGraphSnapshot(resources, diagnostics),
            diagnostics);
    }

    public async Task<AppViewBundleResponse?> GetBundleResponseAsync(
        string bundleIdentity,
        CancellationToken cancellationToken = default)
    {
        var projection = await store.ReadBundleAsync(bundleIdentity, cancellationToken);
        if (projection is null)
        {
            return null;
        }

        return new AppViewBundleResponse(
            projection.Identity,
            projection.Name,
            projection.Description,
            new AppViewSource(projection.SourceCid, projection.SourceRevision),
            projection.IndexedAt,
            projection.ProjectionVersion,
            projection.Members.Select(ToContractMember).ToArray(),
            projection.Diagnostics.Select(ToContractDiagnostic).ToArray());
    }

    private static Bundle ToCoreBundle(StoredBundleProjection projection) =>
        new(
            projection.Name,
            projection.Members.Select(member =>
                new Membership(member.Position, ToCoreReference(member), member.Label)),
            projection.Description,
            projection.SelfUri is null ? null : ResourceUri.Create(projection.SelfUri),
            projection.SelfUri);

    private static ResourceReference ToCoreReference(StoredMember member) =>
        member.Kind switch
        {
            nameof(ResourceReferenceKind.Syndication) =>
                new SyndicationReference(
                    ResourceUri.Create(member.Uri ?? member.Identity),
                    Enum.Parse<SyndicationFormat>(member.Format ?? nameof(SyndicationFormat.Rss)),
                    member.Title),
            nameof(ResourceReferenceKind.AtRecord) =>
                new AtRecordReference(member.Identity, member.Cid, member.Title),
            nameof(ResourceReferenceKind.NestedBundle) =>
                new NestedBundleReference(member.Identity, BundleReferenceMode.Live, member.Title),
            nameof(ResourceReferenceKind.ExplicitDiscovery) =>
                new ExplicitDiscoveryReference(ResourceUri.Create(member.Uri ?? member.Identity), member.Title),
            _ => new UnknownResourceReference(member.Identity, member.Kind, member.Uri)
        };

    private static AppViewMember ToContractMember(StoredMember member) =>
        new(
            member.Position,
            member.Label,
            member.Kind,
            member.Identity,
            member.Uri,
            member.Cid,
            null,
            member.Projection is null
                ? null
                : new AppViewProjectedResource(
                    member.Projection.Kind,
                    member.Projection.Title,
                    member.Projection.Text,
                    member.Projection.Link,
                    member.Projection.Published,
                    member.Projection.Metadata));

    private static AppViewDiagnostic ToContractDiagnostic(GraphDiagnostic diagnostic) =>
        new(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Severity.ToString().ToLowerInvariant(),
            diagnostic.ResourceIdentity,
            diagnostic.Position);
}
