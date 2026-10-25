namespace Convenciones.Identity;

/// <summary>
/// The API contract's error codes (<c>docs/architecture/api-contracts.md §2</c>).
/// </summary>
/// <remarks>
/// Fixed here rather than invented per handler: a contract where each endpoint names its own failures
/// is not a contract, it is a set of conventions that drift apart.
/// </remarks>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string MalformedRequest = "malformed_request";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NoProfile = "no_profile";
    public const string AccountInactive = "account_inactive";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string InvalidState = "invalid_state";
    public const string ConcurrentModification = "concurrent_modification";
    public const string InternalError = "internal_error";
}
