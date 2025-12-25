using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace MoveKind.Umbraco.Controllers
{
    [ApiController]
    [Route("api/schedule")]
    [Authorize] // kräver inloggad Umbraco-medlem (cookie)
    public class MemberScheduleController : ControllerBase
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IEntityService _entityService;

        // ==== ÄNDRA VID BEHOV ==============================================
        private const string SchedulePropertyAlias = "schedule";          // Member property alias (Block List)
        private const string ScheduleItemElementAlias = "scheduleItem";   // Element type alias

        private const string StartTimeAlias = "startTime";               // property alias i elementet
        private const string TitleAlias = "title";
        private const string WorkoutAlias = "workout";

        private const string BlockListLayoutKey = "Umbraco.BlockList";
        // ===================================================================

        public MemberScheduleController(
            IMemberManager memberManager,
            IMemberService memberService,
            IContentTypeService contentTypeService,
            IEntityService entityService)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentTypeService = contentTypeService;
            _entityService = entityService;
        }

        // ---------------- Helpers ----------------

        private async Task<int?> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current is null) return null;

#pragma warning disable CS0618
            var m = _memberService.GetByKey(current.Key);
#pragma warning restore CS0618
            if (m is not null) return m.Id;

            var byUsername = _memberService.GetByUsername(current.UserName);
            if (byUsername is not null) return byUsername.Id;

            if (Guid.TryParse(current.Id, out var guidId))
            {
#pragma warning disable CS0618
                var byKey = _memberService.GetByKey(guidId);
#pragma warning restore CS0618
                if (byKey is not null) return byKey.Id;
            }

            if (int.TryParse(current.Id, out var intId)) return intId;

            return null;
        }

        private Guid GetScheduleItemContentTypeKey()
        {
            var ct = _contentTypeService.Get(ScheduleItemElementAlias);
            if (ct == null)
                throw new InvalidOperationException($"ContentType '{ScheduleItemElementAlias}' hittas inte. Kontrollera alias.");

            return ct.Key;
        }

        private static JsonObject NewEmptyBlockList()
        {
            return new JsonObject
            {
                ["contentData"] = new JsonArray(),
                ["settingsData"] = new JsonArray(),
                ["expose"] = new JsonArray(),
                ["Layout"] = new JsonObject
                {
                    [BlockListLayoutKey] = new JsonArray()
                }
            };
        }

        private static JsonObject EnsureBlockListRoot(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return NewEmptyBlockList();

            try
            {
                var node = JsonNode.Parse(json);
                if (node is JsonObject obj)
                {
                    obj["contentData"] ??= new JsonArray();
                    obj["settingsData"] ??= new JsonArray();
                    obj["expose"] ??= new JsonArray();
                    obj["Layout"] ??= new JsonObject { [BlockListLayoutKey] = new JsonArray() };

                    if (obj["Layout"] is JsonObject layout)
                        layout[BlockListLayoutKey] ??= new JsonArray();

                    return obj;
                }
            }
            catch { /* ignore */ }

            return NewEmptyBlockList();
        }

        // ✅ MATCHAR din editor: "YYYY-MM-DD HH:mm:ss"
        private static string ToUmbracoDateTimeValue(DateTime dt)
            => dt.ToString("yyyy-MM-dd HH:mm:ss");

        // Läser:
        // - "yyyy-MM-dd HH:mm:ss" (rätt)
        // - "yyyy-MM-ddTHH:mm:ss" (ok fallback)
        // - "{\"date\":\"...\"}" (trasigt legacy -> plocka ut date)
        private static bool TryReadUmbracoDateTimeValue(string? raw, out DateTime dt)
        {
            dt = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var s = raw.Trim();

            // trasigt legacy: {"date":"..."}
            if (s.StartsWith("{"))
            {
                try
                {
                    var obj = JsonNode.Parse(s) as JsonObject;
                    var dateStr = obj?["date"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(dateStr) && DateTimeOffset.TryParse(dateStr, out var dto))
                    {
                        dt = dto.DateTime;
                        return true;
                    }
                }
                catch { return false; }
            }

            // normal parse
            return DateTime.TryParse(raw, out dt);
        }

        private static JsonObject? FindValue(JsonArray values, string alias)
        {
            foreach (var v in values)
            {
                if (v is JsonObject o && string.Equals(o["alias"]?.GetValue<string>(), alias, StringComparison.OrdinalIgnoreCase))
                    return o;
            }
            return null;
        }

        private static string? GetValueString(JsonArray values, string alias)
        {
            var o = FindValue(values, alias);
            return o?["value"]?.GetValue<string>();
        }

        private static bool TryGetStartTime(JsonArray values, out DateTime start)
        {
            start = default;
            var raw = GetValueString(values, StartTimeAlias);
            return TryReadUmbracoDateTimeValue(raw, out start);
        }

        private static void UpsertValue(JsonArray values, string alias, string editorAlias, string? value)
        {
            var existing = FindValue(values, alias);

            if (existing is null)
            {
                values.Add(new JsonObject
                {
                    ["editorAlias"] = editorAlias,
                    ["culture"] = null,
                    ["segment"] = null,
                    ["alias"] = alias,
                    ["value"] = value
                });
            }
            else
            {
                existing["value"] = value;
            }
        }

        private static string DocumentUdi(Guid key) => $"umb://document/{key:N}";

        private string? NormalizeWorkoutUdi(string? workoutIdOrUdi)
        {
            if (string.IsNullOrWhiteSpace(workoutIdOrUdi)) return null;

            if (workoutIdOrUdi.StartsWith("umb://", StringComparison.OrdinalIgnoreCase))
                return workoutIdOrUdi;

            if (Guid.TryParse(workoutIdOrUdi, out var g))
                return DocumentUdi(g);

            if (int.TryParse(workoutIdOrUdi, out var id))
            {
                var keyAttempt = _entityService.GetKey(id, UmbracoObjectTypes.Document);
                if (keyAttempt.Success)
                    return DocumentUdi(keyAttempt.Result);
            }

            return null;
        }

        // ---------------- DTOs ----------------
        public record CreateScheduleRequest(DateTime startTime, string? title, string? workoutId);
        public record UpdateScheduleRequest(DateTime? startTime, string? title, string? workoutId);

        // ---------------- Endpoints ----------------

        // GET /api/schedule?from=YYYY-MM-DD&to=YYYY-MM-DD
        [HttpGet("")]
        public async Task<IActionResult> Get([FromQuery] string? from = null, [FromQuery] string? to = null)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var m = _memberService.GetById(memberId.Value);
            if (m is null) return Unauthorized();

            var root = EnsureBlockListRoot(m.GetValue<string>(SchedulePropertyAlias));

            DateTime? fromDate = null;
            DateTime? toDate = null;
            if (!string.IsNullOrWhiteSpace(from) && DateTime.TryParse(from, out var fd)) fromDate = fd.Date;
            if (!string.IsNullOrWhiteSpace(to) && DateTime.TryParse(to, out var td)) toDate = td.Date;

            var result = new List<object>();

            var contentData = root["contentData"] as JsonArray ?? new JsonArray();
            foreach (var node in contentData)
            {
                if (node is not JsonObject item) continue;

                var key = item["key"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(key)) continue;

                var values = item["values"] as JsonArray ?? new JsonArray();
                if (!TryGetStartTime(values, out var start)) continue;

                if (fromDate.HasValue && start.Date < fromDate.Value) continue;
                if (toDate.HasValue && start.Date > toDate.Value) continue;

                result.Add(new
                {
                    key,
                    startTime = start,
                    dateISO = start.ToString("yyyy-MM-dd"),
                    time = start.ToString("HH:mm"),
                    title = GetValueString(values, TitleAlias) ?? "Session",
                    workoutUdi = GetValueString(values, WorkoutAlias)
                });
            }

            return Ok(result.OrderBy(x => ((dynamic)x).startTime));
        }

        // GET /api/schedule/member
        [HttpGet("member")]
        public async Task<IActionResult> GetCurrentMemberWithSchedule()
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var m = _memberService.GetById(memberId.Value);
            if (m is null) return Unauthorized();

            var root = EnsureBlockListRoot(m.GetValue<string>(SchedulePropertyAlias));

            var items = new List<object>();
            var contentData = root["contentData"] as JsonArray ?? new JsonArray();

            foreach (var node in contentData)
            {
                if (node is not JsonObject item) continue;

                var key = item["key"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(key)) continue;

                var values = item["values"] as JsonArray ?? new JsonArray();
                if (!TryGetStartTime(values, out var start)) continue;

                items.Add(new
                {
                    key,
                    startTime = start,
                    dateISO = start.ToString("yyyy-MM-dd"),
                    time = start.ToString("HH:mm"),
                    title = GetValueString(values, TitleAlias) ?? "Session",
                    workoutUdi = GetValueString(values, WorkoutAlias)
                });
            }

            return Ok(new
            {
                member = new
                {
                    id = m.Id,
                    key = m.Key,
                    name = m.Name,
                    email = m.Email
                },
                schedule = items.OrderBy(x => ((dynamic)x).startTime)
            });
        }

        // POST /api/schedule
        [HttpPost("")]
        public async Task<IActionResult> Create([FromBody] CreateScheduleRequest req)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var m = _memberService.GetById(memberId.Value);
            if (m is null) return Unauthorized();

            var contentTypeKey = GetScheduleItemContentTypeKey();
            var root = EnsureBlockListRoot(m.GetValue<string>(SchedulePropertyAlias));

            var itemKey = Guid.NewGuid();
            var workoutUdi = NormalizeWorkoutUdi(req.workoutId);

            // Layout
            var layoutRoot = root["Layout"] as JsonObject ?? new JsonObject();
            root["Layout"] = layoutRoot;

            var layoutArr = layoutRoot[BlockListLayoutKey] as JsonArray ?? new JsonArray();
            layoutRoot[BlockListLayoutKey] = layoutArr;

            layoutArr.Add(new JsonObject
            {
                ["contentUdi"] = null,
                ["settingsUdi"] = null,
                ["contentKey"] = itemKey.ToString("D"),
                ["settingsKey"] = null
            });

            // contentData
            var contentData = root["contentData"] as JsonArray ?? new JsonArray();
            root["contentData"] = contentData;

            var values = new JsonArray();
            UpsertValue(values, StartTimeAlias, "Umbraco.DateTime", ToUmbracoDateTimeValue(req.startTime));
            UpsertValue(values, TitleAlias, "Umbraco.TextBox", req.title ?? "Session");
            UpsertValue(values, WorkoutAlias, "Umbraco.ContentPicker", workoutUdi);

            contentData.Add(new JsonObject
            {
                ["contentTypeKey"] = contentTypeKey.ToString(),
                ["udi"] = null,
                ["key"] = itemKey.ToString("D"),
                ["values"] = values
            });

            // expose (matchar din JSON)
            var expose = root["expose"] as JsonArray ?? new JsonArray();
            root["expose"] = expose;
            expose.Add(new JsonObject
            {
                ["contentKey"] = itemKey.ToString("D"),
                ["culture"] = null,
                ["segment"] = null
            });

            m.SetValue(SchedulePropertyAlias, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            _memberService.Save(m);

            return Created($"/api/schedule/{itemKey:D}", new
            {
                key = itemKey.ToString("D"),
                startTime = req.startTime,
                dateISO = req.startTime.ToString("yyyy-MM-dd"),
                time = req.startTime.ToString("HH:mm"),
                title = req.title ?? "Session",
                workoutUdi
            });
        }

        // PUT /api/schedule/{key}
        [HttpPut("{key}")]
        public async Task<IActionResult> Update([FromRoute] string key, [FromBody] UpdateScheduleRequest req)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var m = _memberService.GetById(memberId.Value);
            if (m is null) return Unauthorized();

            var root = EnsureBlockListRoot(m.GetValue<string>(SchedulePropertyAlias));
            var contentData = root["contentData"] as JsonArray ?? new JsonArray();

            JsonObject? found = null;
            foreach (var node in contentData)
            {
                if (node is JsonObject o &&
                    string.Equals(o["key"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))
                {
                    found = o;
                    break;
                }
            }

            if (found is null) return NotFound(new { error = "Schedule item not found" });

            var values = found["values"] as JsonArray ?? new JsonArray();
            found["values"] = values;

            if (req.startTime.HasValue)
                UpsertValue(values, StartTimeAlias, "Umbraco.DateTime", ToUmbracoDateTimeValue(req.startTime.Value));

            if (req.title is not null)
                UpsertValue(values, TitleAlias, "Umbraco.TextBox", req.title);

            if (req.workoutId is not null)
                UpsertValue(values, WorkoutAlias, "Umbraco.ContentPicker", NormalizeWorkoutUdi(req.workoutId));

            m.SetValue(SchedulePropertyAlias, root.ToJsonString());
            _memberService.Save(m);

            return Ok(new { updated = true });
        }

        // DELETE /api/schedule/{key}
        [HttpDelete("{key}")]
        public async Task<IActionResult> Delete([FromRoute] string key)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var m = _memberService.GetById(memberId.Value);
            if (m is null) return Unauthorized();

            var root = EnsureBlockListRoot(m.GetValue<string>(SchedulePropertyAlias));

            var contentData = root["contentData"] as JsonArray ?? new JsonArray();
            root["contentData"] = contentData;

            var removed = false;
            for (int i = 0; i < contentData.Count; i++)
            {
                if (contentData[i] is JsonObject o &&
                    string.Equals(o["key"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))
                {
                    contentData.RemoveAt(i);
                    removed = true;
                    break;
                }
            }

            // Layout entry bort (matchar contentKey)
            var layoutRoot = root["Layout"] as JsonObject;
            var layoutArr = layoutRoot?[BlockListLayoutKey] as JsonArray;

            if (layoutArr is not null)
            {
                for (int i = 0; i < layoutArr.Count; i++)
                {
                    if (layoutArr[i] is JsonObject lo &&
                        string.Equals(lo["contentKey"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        layoutArr.RemoveAt(i);
                        break;
                    }
                }
            }

            // expose entry bort
            var expose = root["expose"] as JsonArray;
            if (expose is not null)
            {
                for (int i = 0; i < expose.Count; i++)
                {
                    if (expose[i] is JsonObject ex &&
                        string.Equals(ex["contentKey"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        expose.RemoveAt(i);
                        break;
                    }
                }
            }

            if (!removed) return NoContent();

            m.SetValue(SchedulePropertyAlias, root.ToJsonString());
            _memberService.Save(m);

            return NoContent();
        }

        // POST /api/schedule/repair
        // Fixar gamla trasiga startTime-värden:
        // - {"date":"..."}  -> "yyyy-MM-dd HH:mm:ss"
        // - "yyyy-MM-ddTHH:mm:ss" -> "yyyy-MM-dd HH:mm:ss"
        [HttpPost("repair")]
        public async Task<IActionResult> Repair()
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId is null) return Unauthorized();

            var m = _memberService.GetById(memberId.Value);
            if (m is null) return Unauthorized();

            var root = EnsureBlockListRoot(m.GetValue<string>(SchedulePropertyAlias));
            var contentData = root["contentData"] as JsonArray ?? new JsonArray();

            var changed = 0;

            foreach (var node in contentData)
            {
                if (node is not JsonObject item) continue;

                var values = item["values"] as JsonArray;
                if (values is null) continue;

                var startObj = FindValue(values, StartTimeAlias);
                var raw = startObj?["value"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // Om den är JSON eller innehåller T, parsea och skriv om i rätt format
                if (raw.TrimStart().StartsWith("{") || raw.Contains('T'))
                {
                    if (TryReadUmbracoDateTimeValue(raw, out var dt))
                    {
                        startObj!["value"] = ToUmbracoDateTimeValue(dt);
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                m.SetValue(SchedulePropertyAlias, root.ToJsonString());
                _memberService.Save(m);
            }

            return Ok(new { changed });
        }
    }
}
