using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jibo.Cloud.Api.Hosting.Config;

internal static class AdminConfigEndpoints
{
    private const string AdminSessionDeviceId = "portal-admin";

    internal static void MapAdminConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/api/portal/config", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            IConfiguration configuration,
            AdminConfigOverlayStore overlayStore) =>
        {
            if (!TryAuthorizeAdmin(request, null, portalSessionService))
                return Results.Unauthorized();

            var overlay = overlayStore.ReadFlatValues();
            var settings = AdminConfigDescriptorRegistry.All
                .Select(descriptor =>
                {
                    var effective = configuration[descriptor.Key];
                    var hasOverlay = overlay.TryGetValue(descriptor.Key, out var overlayValue);
                    return new
                    {
                        key = descriptor.Key,
                        section = descriptor.Section,
                        label = descriptor.Label,
                        description = descriptor.Description,
                        valueType = descriptor.ValueType.ToString().ToLowerInvariant(),
                        defaultValue = descriptor.DefaultValue,
                        isSecret = descriptor.IsSecret,
                        requiresRestart = descriptor.RequiresRestart,
                        placeholder = descriptor.Placeholder,
                        value = effective,
                        overlayValue = hasOverlay ? overlayValue : null,
                        isOverridden = hasOverlay,
                        source = ResolveSource(hasOverlay, effective, descriptor.DefaultValue)
                    };
                })
                .ToArray();

            return Results.Json(new
            {
                overlayPath = overlayStore.OverlayPath,
                restartRequired = settings.Any(setting => setting.isOverridden && setting.requiresRestart),
                sections = settings
                    .GroupBy(setting => setting.section)
                    .Select(group => new
                    {
                        name = group.Key,
                        settings = group.ToArray()
                    })
                    .ToArray()
            });
        });

        app.MapPut("/api/portal/config", (
            [FromBody] AdminConfigUpdateRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            AdminConfigOverlayStore overlayStore) =>
        {
            if (!TryAuthorizeAdmin(httpRequest, request.PortalSessionToken, portalSessionService))
                return Results.Unauthorized();

            if (request.Values is null || request.Values.Count == 0)
                return Results.BadRequest(new { error = "values is required." });

            var updates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();
            var invalid = new List<string>();

            foreach (var (key, value) in request.Values)
            {
                var descriptor = AdminConfigDescriptorRegistry.Find(key);
                if (descriptor is null)
                {
                    unknown.Add(key);
                    continue;
                }

                if (value is null)
                {
                    updates[descriptor.Key] = null;
                    continue;
                }

                if (!TryNormalizeValue(descriptor, value, out var normalized, out var error))
                {
                    invalid.Add($"{key}: {error}");
                    continue;
                }

                updates[descriptor.Key] = normalized;
            }

            if (unknown.Count > 0)
                return Results.BadRequest(new { error = "Unknown configuration keys.", keys = unknown });
            if (invalid.Count > 0)
                return Results.BadRequest(new { error = "Invalid configuration values.", details = invalid });

            overlayStore.Upsert(updates);
            var restartRequired = updates.Keys
                .Select(AdminConfigDescriptorRegistry.Find)
                .Any(descriptor => descriptor?.RequiresRestart == true);

            return Results.Json(new
            {
                ok = true,
                updatedCount = updates.Count,
                restartRequired,
                overlayPath = overlayStore.OverlayPath,
                message = restartRequired
                    ? "Configuration saved. Restart the BEefy process for changes to take effect."
                    : "Configuration saved."
            });
        });

        app.MapDelete("/api/portal/config", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            AdminConfigOverlayStore overlayStore) =>
        {
            if (!TryAuthorizeAdmin(request, null, portalSessionService))
                return Results.Unauthorized();

            overlayStore.Clear();
            return Results.Json(new
            {
                ok = true,
                restartRequired = true,
                message = "Admin config overlay cleared. Restart the BEefy process to reload baseline settings."
            });
        });
    }

    private static bool TryAuthorizeAdmin(
        HttpRequest request,
        string? portalSessionToken,
        PortalSessionService portalSessionService)
    {
        var token = ResolveToken(request, portalSessionToken);
        var session = portalSessionService.TryGetSession(token);
        return session is not null &&
               string.Equals(session.DeviceId, AdminSessionDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveToken(HttpRequest request, string? portalSessionToken)
    {
        var token = request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(token) &&
            token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        token ??= request.Query["portalSessionToken"].FirstOrDefault();
        token ??= portalSessionToken;
        return token;
    }

    private static string ResolveSource(bool hasOverlay, string? effective, string? defaultValue)
    {
        if (hasOverlay) return "overlay";
        if (!string.IsNullOrWhiteSpace(effective) &&
            !string.Equals(effective, defaultValue, StringComparison.Ordinal))
            return "configured";
        if (!string.IsNullOrWhiteSpace(effective))
            return "default";
        return "unset";
    }

    private static bool TryNormalizeValue(
        AdminConfigDescriptor descriptor,
        string value,
        out string normalized,
        out string error)
    {
        normalized = value.Trim();
        error = string.Empty;

        switch (descriptor.ValueType)
        {
            case AdminConfigValueType.Boolean:
                if (bool.TryParse(normalized, out var boolean))
                {
                    normalized = boolean ? "true" : "false";
                    return true;
                }

                error = "expected true or false.";
                return false;

            case AdminConfigValueType.Integer:
                if (int.TryParse(normalized, out _))
                    return true;
                error = "expected an integer.";
                return false;

            case AdminConfigValueType.Number:
                if (double.TryParse(normalized, out _))
                    return true;
                error = "expected a number.";
                return false;

            default:
                return true;
        }
    }

    private sealed class AdminConfigUpdateRequest
    {
        public string? PortalSessionToken { get; set; }
        public Dictionary<string, string?>? Values { get; set; }
    }
}
