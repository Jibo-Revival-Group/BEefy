using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Jibo.Cloud.Api.Hosting;

public static class LoopMemberPhotoEndpoints
{
    public static IEndpointRouteBuilder MapLoopMemberPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/loop-member-photo/{memberId}/{fileName}", async (
            string memberId,
            string fileName,
            HttpRequest request,
            ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore,
            LoopMemberPhotoUrlSigner photoUrlSigner,
            PortalSessionService portalSessionService,
            CancellationToken cancellationToken) =>
        {
            var contentHash = ExtractContentHash(fileName);
            if (string.IsNullOrWhiteSpace(contentHash))
                return Results.NotFound();

            var signatureOk = photoUrlSigner.TryValidate(
                memberId,
                contentHash,
                request.Query["expires"].FirstOrDefault(),
                request.Query["signature"].FirstOrDefault());
            var sessionOk = ResolvePortalSession(request, portalSessionService) is not null;
            if (!signatureOk && !sessionOk)
                return Results.NotFound();

            var member = FindMember(cloudStateStore, memberId);
            if (member is null ||
                string.IsNullOrWhiteSpace(member.PhotoContentHash) ||
                !member.PhotoContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            var storePath = LoopMemberPhotoProcessor.MediaStorePath(member.LoopId, member.Id);
            var stored = await mediaContentStore.LoadAsync(storePath, cancellationToken);
            if (stored is null || stored.Content.Length == 0)
                return Results.NotFound();

            var contentType = string.IsNullOrWhiteSpace(member.PhotoContentType)
                ? stored.ContentType ?? "image/jpeg"
                : member.PhotoContentType;
            return Results.File(stored.Content, contentType, enableRangeProcessing: false);
        });

        return app;
    }

    private static string? ExtractContentHash(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = fileName.Trim();
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.All(static ch => char.IsAsciiHexDigit(ch)) && name.Length >= 16
            ? name.ToLowerInvariant()
            : null;
    }

    private static LoopMemberRecord? FindMember(ICloudStateStore store, string memberId)
    {
        foreach (var loop in store.GetLoops())
        {
            var member = store.GetLoopMembers(loop.LoopId)
                .FirstOrDefault(item => item.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase));
            if (member is not null) return member;
        }

        return null;
    }

    private static PortalSessionService.PortalSession? ResolvePortalSession(
        HttpRequest request,
        PortalSessionService portalSessionService)
    {
        var token = request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(token) &&
            token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();
        token ??= request.Query["portalSessionToken"].FirstOrDefault();
        return portalSessionService.TryGetSession(token);
    }
}
