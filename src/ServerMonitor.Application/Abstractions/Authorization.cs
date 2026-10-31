namespace ServerMonitor.Application.Abstractions;

public static class CurrentUserExtensions
{
    /// <summary>
    /// Backend authorisation check (defence in depth — pages are also protected by [Authorize]).
    /// Throws when the caller has none of the given roles.
    /// </summary>
    public static void Demand(this ICurrentUser user, params string[] roles)
    {
        if (user.UserName is null || !roles.Any(user.IsInRole))
            throw new UnauthorizedAccessException($"This action requires one of the roles: {string.Join(", ", roles)}.");
    }
}
