using System.Net;

namespace AniMeido.Plugin.Player.Sources.Web;

internal static class WebPageAccessEvaluator
{
    public static SourceResolutionFailureKind? Classify(
        HttpStatusCode statusCode,
        WebPageInteractionKind interaction)
    {
        if (interaction == WebPageInteractionKind.Login)
        {
            return SourceResolutionFailureKind.Authentication;
        }

        if (interaction == WebPageInteractionKind.HumanVerification)
        {
            return SourceResolutionFailureKind.HumanVerification;
        }

        return statusCode switch
        {
            HttpStatusCode.Unauthorized
                => SourceResolutionFailureKind.Authentication,
            HttpStatusCode.Forbidden
                => SourceResolutionFailureKind.AccessDenied,
            HttpStatusCode.NotFound
                => SourceResolutionFailureKind.NotFound,
            HttpStatusCode.TooManyRequests
                => SourceResolutionFailureKind.RateLimited,
            _ => null,
        };
    }
}
