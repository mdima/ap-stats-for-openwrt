using Microsoft.AspNetCore.Authorization;

namespace OpenWrtApStats.Services.Auth;

/// <summary>D-01 marker: signals the conditional fallback policy.</summary>
internal sealed record ConditionalAuthRequirement : IAuthorizationRequirement;
