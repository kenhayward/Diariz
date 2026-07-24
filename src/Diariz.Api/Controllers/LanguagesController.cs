using Diariz.Api.Contracts;
using Diariz.Api.Localization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>Serves the supported-language list (static reference data). Anonymous so the signup pages
/// can offer a language selector before the user has an account.</summary>
[ApiController]
[Route("api/languages")]
public class LanguagesController : ControllerBase
{
    [HttpGet]
    [EndpointSummary("List supported languages")]
    [EndpointDescription(
        "The interface languages this build ships, each with its code, English name, and native name. Static " +
        "reference data - the same for everyone, so it can be cached.\n\n" +
        "**No authentication required**, so a sign-up or account-setup page can offer a language picker " +
        "before an account exists. These codes are also what the profile's language fields and the " +
        "translation endpoints accept.")]
    public IReadOnlyList<LanguageDto> Get() => SupportedLanguages.All;
}
