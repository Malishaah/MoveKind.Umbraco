using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Security;
using Umbraco.Extensions;

namespace MoveKind.Umbraco.Controllers;

[ApiController]
[Route("api/members")]
public class AuthApiController : ControllerBase
{
    private readonly MemberManager _memberManager;
    private readonly MemberSignInManager _signInManager;
    private readonly IMemberService _memberService;
    private readonly IRelationService _relationService;
    private readonly IUmbracoContextFactory _umbracoContextFactory;

    // ✅ Member-property-alias (MÅSTE matcha Umbraco Member Type aliases exakt)
    private const string Alias_LargerText = "largerText";
    private const string Alias_HighContrast = "highContrast";
    private const string Alias_LightMode = "lightMode";
    private const string Alias_CaptionsOnByDefault = "captionsOnByDefault";
    private const string Alias_RemindersEnabled = "remindersEnabled";
    private const string Alias_DefaultReminderTime = "defaultReminderTime";

    // Favorites relation
    private const string RelationAlias = "memberFavoritesWorkout";

    public AuthApiController(
        MemberManager memberManager,
        MemberSignInManager signInManager,
        IMemberService memberService,
        IRelationService relationService,
        IUmbracoContextFactory umbracoContextFactory)
    {
        _memberManager = memberManager;
        _signInManager = signInManager;
        _memberService = memberService;
        _relationService = relationService;
        _umbracoContextFactory = umbracoContextFactory;
    }

    // ---------- DTOs ----------
    public record RegisterDto(string Email, string Password, string? Username, string? Name);
    public record LoginDto(string UsernameOrEmail, string Password, bool RememberMe = true);

    public record ProfileDto(
        string Email,
        string Name,
        bool LargerText,
        bool HighContrast,
        bool LightMode,
        bool CaptionsOnByDefault,
        bool RemindersEnabled,
        string DefaultReminderTime // "HH:mm"
    );

    public record ChangePasswordDto(string CurrentPassword, string NewPassword);

    // ---------- Helpers ----------
    private static bool HasProperty(IContentBase c, string alias)
        => c.Properties?.Any(p => p.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)) == true;

    private static string NormalizeTimeHHmm(string? value, string fallback = "08:00")
    {
        var v = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(v)) return fallback;

        // Accept HH:mm only
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{2}:\d{2}$"))
            return v;

        return fallback;
    }

    private static T? SafeGetValue<T>(IContentBase c, string alias)
    {
        if (!HasProperty(c, alias)) return default;
        return c.GetValue<T>(alias);
    }

    private static void SafeSetValue(IContentBase c, string alias, object? value)
    {
        if (!HasProperty(c, alias)) return;
        c.SetValue(alias, value);
    }

    private async Task<IMember?> GetCurrentMemberEntityAsync()
    {
        var current = await _memberManager.GetCurrentMemberAsync();
        if (current is null) return null;

        // current.Key är säkrast i Umbraco 13+
        return _memberService.GetByKey(current.Key);
    }

    // ---------- Register ----------
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        var username = string.IsNullOrWhiteSpace(dto.Username) ? dto.Email : dto.Username!.Trim();

        // Member type alias: "member" (ändra om din heter något annat)
        var identityUser = MemberIdentityUser.CreateNew(username, dto.Email, "member", isApproved: true);

        var createResult = await _memberManager.CreateAsync(identityUser, dto.Password);
        if (!createResult.Succeeded)
        {
            return BadRequest(new
            {
                ok = false,
                errors = createResult.Errors.Select(e => e.Description)
            });
        }

        // Sätt Name + defaults på IMember
        var m = _memberService.GetByKey(identityUser.Key);
        if (m is not null)
        {
            if (!string.IsNullOrWhiteSpace(dto.Name))
                m.Name = dto.Name!.Trim();

            // defaultReminderTime ska vara string "HH:mm" (inte DateTime)
            SafeSetValue(m, Alias_DefaultReminderTime, "08:00");

            _memberService.Save(m);
        }

        // Auto-login
        var signIn = await _signInManager.PasswordSignInAsync(username, dto.Password, isPersistent: true, lockoutOnFailure: false);
        return Ok(new { ok = true, username, autoSignedIn = signIn.Succeeded });
    }

    // ---------- Login ----------
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        // tillåt e-post eller username
        var byEmail = _memberService.GetByEmail(dto.UsernameOrEmail);
        var userName = byEmail?.Username ?? dto.UsernameOrEmail;

        var res = await _signInManager.PasswordSignInAsync(userName, dto.Password, dto.RememberMe, lockoutOnFailure: false);
        if (!res.Succeeded) return Unauthorized(new { ok = false });

        return Ok(new { ok = true, username = userName });
    }

    // ---------- Logout ----------
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return Ok(new { ok = true });
    }

    // ---------- Me (GET) ----------
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var m = await GetCurrentMemberEntityAsync();
        if (m is null) return Unauthorized();

        var dto = new ProfileDto(
            Email: m.Email ?? "",
            Name: m.Name ?? "",
            LargerText: SafeGetValue<bool?>(m, Alias_LargerText) ?? false,
            HighContrast: SafeGetValue<bool?>(m, Alias_HighContrast) ?? false,
            LightMode: SafeGetValue<bool?>(m, Alias_LightMode) ?? false,
            CaptionsOnByDefault: SafeGetValue<bool?>(m, Alias_CaptionsOnByDefault) ?? false,
            RemindersEnabled: SafeGetValue<bool?>(m, Alias_RemindersEnabled) ?? false,
            DefaultReminderTime: NormalizeTimeHHmm(SafeGetValue<string>(m, Alias_DefaultReminderTime) ?? "08:00")
        );

        return Ok(dto);
    }

    // ---------- Me (PUT) ----------
    [HttpPut("me")]
    public async Task<IActionResult> UpdateMe([FromBody] ProfileDto dto)
    {
        var m = await GetCurrentMemberEntityAsync();
        if (m is null) return Unauthorized();

        // Uppdatera grundfält
        if (!string.IsNullOrWhiteSpace(dto.Name)) m.Name = dto.Name.Trim();
        if (!string.IsNullOrWhiteSpace(dto.Email)) m.Email = dto.Email.Trim();

        // Uppdatera bara om property finns
        SafeSetValue(m, Alias_LargerText, dto.LargerText);
        SafeSetValue(m, Alias_HighContrast, dto.HighContrast);
        SafeSetValue(m, Alias_LightMode, dto.LightMode);
        SafeSetValue(m, Alias_CaptionsOnByDefault, dto.CaptionsOnByDefault);
        SafeSetValue(m, Alias_RemindersEnabled, dto.RemindersEnabled);

        // ⚠️ Viktigt: defaultReminderTime är string "HH:mm"
        SafeSetValue(m, Alias_DefaultReminderTime, NormalizeTimeHHmm(dto.DefaultReminderTime, "8:00"));

        _memberService.Save(m);

        return Ok(new { ok = true });
    }

    // ---------- Favorites (GET) ----------
    [HttpGet("favorites")]
    public async Task<IActionResult> GetFavorites()
    {
        var m = await GetCurrentMemberEntityAsync();
        if (m is null) return Unauthorized();

        var rels = (_relationService.GetByParentId(m.Id) ?? Enumerable.Empty<IRelation>())
            .Where(r => r.RelationType?.Alias == RelationAlias);

        var ids = rels.Select(r => r.ChildId).Distinct().ToArray();

        using var cref = _umbracoContextFactory.EnsureUmbracoContext();
        var contentCache = cref.UmbracoContext.Content;

        var nodes = ids
            .Select(id => contentCache?.GetById(id))
            .Where(n => n is not null)
            .Select(n => new
            {
                id = n!.Id,
                key = n!.Key,
                name = n!.Name,
                url = n!.Url()
            });

        return Ok(nodes);
    }

    // ---------- Change password ----------
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        var current = await _memberManager.GetCurrentMemberAsync();
        if (current is null) return Unauthorized();

        var result = await _memberManager.ChangePasswordAsync(current, dto.CurrentPassword, dto.NewPassword);
        if (!result.Succeeded)
        {
            return BadRequest(new
            {
                ok = false,
                errors = result.Errors.Select(e => e.Description)
            });
        }

        // uppdatera cookies
        await _signInManager.SignInAsync(current, isPersistent: true);

        return Ok(new { ok = true });
    }
}
