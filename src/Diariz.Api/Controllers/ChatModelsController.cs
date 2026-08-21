using Diariz.Api.Contracts;
using Diariz.Api.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>The models a chat user may choose between.
///
/// Its own controller rather than an action on <see cref="ChatController"/>: this route is read by every
/// signed-in user, while ChatController's actions are all per-user data operations. Keeping the surface
/// that exposes platform configuration small and separate is what makes the "no endpoint, no key" contract
/// easy to see and hard to erode.</summary>
[ApiController]
[Route("api/chat/models")]
[Authorize]
public class ChatModelsController(IChatModelCatalog catalog) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("List the models you can chat with")]
    [EndpointDescription(
        "The models a Platform Administrator offers for chat: the default first, then the rest by name. " +
        "Pass an `id` from this list as `modelId` on a chat request to have that model answer the turn; a " +
        "model that is not in this list is ignored and the default answers instead.\n\n" +
        "Endpoints and API keys are never returned - those are administrator-only.")]
    public async Task<ActionResult<List<ChatModelDto>>> List(CancellationToken ct = default)
    {
        var options = await catalog.ListAsync(ct);
        return options
            .Select(o => new ChatModelDto(
                o.Id, o.Label, o.Name, o.ContextLength, o.IsDefault, o.SupportsImages,
                o.SupportsTools, o.Description))
            .ToList();
    }
}
