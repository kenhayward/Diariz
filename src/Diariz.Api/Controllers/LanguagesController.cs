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
    public IReadOnlyList<LanguageDto> Get() => SupportedLanguages.All;
}
