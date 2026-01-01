using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Security;

namespace MoveKind.Umbraco.Controllers;

[ApiController]
[Route("api/personalization")]
public class PersonalizationController : ControllerBase
{
    private readonly MemberManager _memberManager;
    private readonly IMemberService _memberService;

    // Member property aliases (måste matcha Umbraco exakt)
    private const string Alias_Needs = "needs";       // multi dropdown
    private const string Alias_Level = "goalLevel";   // dropdown
    private const string Alias_Skipped = "personalizationSkipped"; // om du har den

    public PersonalizationController(MemberManager memberManager, IMemberService memberService)
    {
        _memberManager = memberManager;
        _memberService = memberService;
    }

    // ---------- DTO ----------
    public record PersonalizationDto(
        string[] PersonalizationNeeds,
        string PersonalizationLevel,
        bool PersonalizationSkipped
    );

    // ---------- Helpers ----------
    private static bool HasProperty(IContentBase c, string alias)
        => c.Properties?.Any(p => p.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)) == true;

    private async Task<IMember?> GetCurrentMemberEntityAsync()
    {
        var current = await _memberManager.GetCurrentMemberAsync();
        if (current is null) return null;
        return _memberService.GetByKey(current.Key);
    }

    private static string NormalizeLevel(string? v, string fallback = "Easy")
    {
        var x = (v ?? "").Trim();
        return x is "Easy" or "Medium" or "Advanced" ? x : fallback;
    }

    private static string[] NormalizeNeeds(IEnumerable<string>? needs)
        => (needs ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // Read multi dropdown (Umbraco sparar ofta JSON array string)
    private static string[] ReadNeeds(IContentBase m, string alias)
    {
        var raw = m.GetValue<string>(alias);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        raw = raw.Trim();

        if (raw.StartsWith("["))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(raw);
                return NormalizeNeeds(arr);
            }
            catch { return Array.Empty<string>(); }
        }

        // fallback om den råkar vara CSV
        return NormalizeNeeds(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    // Write multi dropdown som JSON array string
    private static void WriteNeeds(IContentBase m, string alias, string[] needs)
    {
        var normalized = NormalizeNeeds(needs);
        var json = JsonSerializer.Serialize(normalized); // ["Knee","Back"]
        m.SetValue(alias, json);
    }

    // ---------- GET ----------
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var m = await GetCurrentMemberEntityAsync();
        if (m is null) return Unauthorized();

        var needs = HasProperty(m, Alias_Needs) ? ReadNeeds(m, Alias_Needs) : Array.Empty<string>();
        var level = HasProperty(m, Alias_Level) ? NormalizeLevel(m.GetValue<string>(Alias_Level)) : "Easy";
        var skipped = HasProperty(m, Alias_Skipped) ? (m.GetValue<bool?>(Alias_Skipped) ?? false) : false;

        return Ok(new PersonalizationDto(needs, level, skipped));
    }

    // ---------- PUT ----------
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] PersonalizationDto dto)
    {
        var m = await GetCurrentMemberEntityAsync();
        if (m is null) return Unauthorized();

        if (HasProperty(m, Alias_Needs))
            WriteNeeds(m, Alias_Needs, dto.PersonalizationNeeds ?? Array.Empty<string>());

        if (HasProperty(m, Alias_Level))
            m.SetValue(Alias_Level, NormalizeLevel(dto.PersonalizationLevel));

        if (HasProperty(m, Alias_Skipped))
            m.SetValue(Alias_Skipped, dto.PersonalizationSkipped);

        _memberService.Save(m);
        return Ok(new { ok = true });
    }
}
