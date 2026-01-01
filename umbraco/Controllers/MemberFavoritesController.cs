using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Threading.Tasks;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace MoveKind.Umbraco.Controllers
{
    [ApiController]
    [Route("api/favorites")]
    [Authorize] // kräver inloggad Umbraco-medlem (cookie)
    public class MemberFavoritesController : ControllerBase
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IRelationService _relationService;
        private readonly IUmbracoContextFactory _umbracoContextFactory;
        private readonly IEntityService _entityService;

        // Måste finnas i databasen (skapad av din Composer)
        private const string RelationAlias = "memberFavoritesWorkout";

        public MemberFavoritesController(
            IMemberManager memberManager,
            IMemberService memberService,
            IRelationService relationService,
            IUmbracoContextFactory umbracoContextFactory,
            IEntityService entityService)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _relationService = relationService;
            _umbracoContextFactory = umbracoContextFactory;
            _entityService = entityService;
        }

        // --- Helpers ---------------------------------------------------------

        // Hämta nuvarande medlem som INT-id (oavsett om current.Id är string/guid)
        private async Task<int?> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current is null) return null;

            // 1) Säkraste vägen: använd Key (Guid) -> IMember -> int Id
            var m = _memberService.GetByKey(current.Key);
            if (m is not null) return m.Id;

            // 2) Fallback via username
            var byUsername = _memberService.GetByUsername(current.UserName);
            if (byUsername is not null) return byUsername.Id;

            // 3) Om Id råkar vara en Guid i stringform
            if (Guid.TryParse(current.Id, out var guidId))
            {
                var byKey = _memberService.GetByKey(guidId);
                if (byKey is not null) return byKey.Id;
            }

            // 4) Om Id redan är ett int i stringform
            if (int.TryParse(current.Id, out var intId)) return intId;

            return null;
        }

        // Konvertera ett content-id i form av int/GUID/UDI -> int nodeId
        private int? ToIntContentId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;

            // 1) Redan int?
            if (int.TryParse(id, out var asInt)) return asInt;

            // 2) GUID?
            if (Guid.TryParse(id, out var key))
            {
                var attempt = _entityService.GetId(key, UmbracoObjectTypes.Document);
                if (attempt.Success) return attempt.Result;
            }

            // 3) UDI? (umb://document/{guid})
            if (UdiParser.TryParse(id, out Udi? udi))
            {
                var attempt = _entityService.GetId(udi);
                if (attempt.Success) return attempt.Result;
            }

            return null;
        }

        private IRelationType? GetRelationType()
            => _relationService.GetRelationTypeByAlias(RelationAlias);

        // --- Endpoints -------------------------------------------------------

        // Lista favoriter (objekt)
        [HttpGet("")]
        public async Task<IActionResult> GetFavorites()
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var rels = (_relationService.GetByParentId(memberId.Value) ?? Enumerable.Empty<IRelation>())
                .Where(r => r.RelationType?.Alias == RelationAlias);

            var ids = rels.Select(r => r.ChildId).Distinct().ToArray();

            using var cref = _umbracoContextFactory.EnsureUmbracoContext();
            var cache = cref.UmbracoContext.Content;

            var nodes = ids
                .Select(id => cache?.GetById(id))
                .Where(n => n is not null)
                .Select(n =>
                {
                    // Bild(er): funkar för både single- och multi-picker
                    var firstImage = n!.Value<MediaWithCrops>("image");
                    var manyImages = n!.Value<IEnumerable<MediaWithCrops>>("image") ?? Enumerable.Empty<MediaWithCrops>();

                    return new
                    {
                        key = n!.Key,
                        id = n!.Id,
                        name = n!.Name,
                        url = n!.Url(),

                        // Dina egna fält på "workout"
                        title = n!.Value<string>("title"),
                        duration = n!.Value<int?>("duration"),
                        level = n!.Value<string>("levelEasyMediumAdvanced"),
                        focus = n!.Value<IEnumerable<string>>("focus") ?? Enumerable.Empty<string>(),
                        position = n!.Value<IEnumerable<string>>("position") ?? Enumerable.Empty<string>(),
                        floorFriendly = n!.Value<bool?>("floorFriendly") ?? false,
                        chairFriendly = n!.Value<bool?>("chairFriendly") ?? false,
                        description = n!.Value<string>("description"), // (richtext -> string)

                        // Bild-URL: första bilden (om du bara vill ha en)
                        imageUrl = firstImage?.MediaUrl(),

                        // Alla bild-URL:er om multi-picker används
                        imageUrls = manyImages
                            .Select(m => m?.MediaUrl())
                            .Where(u => !string.IsNullOrWhiteSpace(u))
                            .ToArray()
                    };
                });

            return Ok(nodes);
        }

        // Lista endast favoriter som ID:n (int)
        [HttpGet("ids")]
        public async Task<IActionResult> GetFavoriteIds()
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var rels = (_relationService.GetByParentId(memberId.Value) ?? Enumerable.Empty<IRelation>())
                .Where(r => r.RelationType?.Alias == RelationAlias);

            var ids = rels.Select(r => r.ChildId).Distinct().ToArray();

            using var cref = _umbracoContextFactory.EnsureUmbracoContext();
            var cache = cref.UmbracoContext.Content;

            var keys = ids
                .Select(id => cache?.GetById(id)?.Key)
                .Where(k => k.HasValue)
                .Select(k => k!.Value.ToString())
                .Distinct()
                .ToArray();

            return Ok(keys);
        }

        // Lägg till favorit (workoutId kan vara int/guid/udi)
        [HttpPost("{workoutId}")]
        public async Task<IActionResult> AddFavorite([FromRoute] string workoutId)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var nodeId = ToIntContentId(workoutId);
            if (nodeId is null) return BadRequest(new { error = "Ogiltigt workout-id" });

            var already = (_relationService.GetByParentId(memberId.Value) ?? Enumerable.Empty<IRelation>())
                .Any(r => r.RelationType?.Alias == RelationAlias && r.ChildId == nodeId.Value);
            if (already) return NoContent();

            var rt = GetRelationType();
            if (rt is null) return BadRequest($"RelationType '{RelationAlias}' saknas.");

            _relationService.Save(new Relation(memberId.Value, nodeId.Value, rt));
            return Created($"/api/favorites/{nodeId}", new { workoutId = nodeId.Value });
        }

        // Ta bort favorit
        [HttpDelete("{workoutId}")]
        public async Task<IActionResult> RemoveFavorite([FromRoute] string workoutId)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var nodeId = ToIntContentId(workoutId);
            if (nodeId is null) return BadRequest(new { error = "Ogiltigt workout-id" });

            var rels = (_relationService.GetByParentId(memberId.Value) ?? Enumerable.Empty<IRelation>())
                .Where(r => r.RelationType?.Alias == RelationAlias && r.ChildId == nodeId.Value)
                .ToList();

            if (rels.Count == 0) return NoContent();

            foreach (var r in rels) _relationService.Delete(r);
            return NoContent();
        }

        // Toggle favorit
        [HttpPost("{workoutId}/toggle")]
        public async Task<IActionResult> Toggle([FromRoute] string workoutId)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var nodeId = ToIntContentId(workoutId);
            if (nodeId is null) return BadRequest(new { error = "Ogiltigt workout-id" });

            var existing = (_relationService.GetByParentId(memberId.Value) ?? Enumerable.Empty<IRelation>())
                .FirstOrDefault(r => r.RelationType?.Alias == RelationAlias && r.ChildId == nodeId.Value);

            if (existing is null)
            {
                var rt = GetRelationType();
                if (rt is null) return BadRequest($"RelationType '{RelationAlias}' saknas.");

                _relationService.Save(new Relation(memberId.Value, nodeId.Value, rt));
                return Ok(new { workoutId = nodeId.Value, favorited = true });
            }

            _relationService.Delete(existing);
            return Ok(new { workoutId = nodeId.Value, favorited = false });
        }
    }
}
