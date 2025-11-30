using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Security;
using Umbraco.Cms.Core.Security; // <-- behövs för MemberIdentityUser, IMemberManager, IMemberSignInManager


namespace MoveKind.Umbraco.Controllers;

[ApiController]
[Route("api/members")]
public class AuthApiController : ControllerBase
{
    private readonly MemberManager _memberManager;
    private readonly MemberSignInManager _signInManager;
    private readonly IMemberService _memberService;

    // Dina Member-property-alias:
    private const string Alias_LargerText        = "largerText";
    private const string Alias_HighContrast      = "highContrast";
    private const string Alias_LightMode         = "lightMode";
    private const string Alias_CaptionsDefault   = "captionsDefault";
    private const string Alias_RemindersEnabled  = "remindersEnabled";
    private const string Alias_DefaultReminder   = "defaultReminderTime";

    public AuthApiController(
        MemberManager memberManager,
        MemberSignInManager signInManager,
        IMemberService memberService)
    {
        _memberManager  = memberManager;
        _signInManager  = signInManager;
        _memberService  = memberService;
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
        string DefaultReminderTime
    );
    public record ChangePasswordDto(string CurrentPassword, string NewPassword);

    // ---------- Register ----------
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        var username = string.IsNullOrWhiteSpace(dto.Username) ? dto.Email : dto.Username!;

        // Skapa identitetsanvändare (kopplas till MemberType "member", byt vid behov)
        var identityUser = MemberIdentityUser.CreateNew(username, dto.Email, "member", isApproved: true);

        var createResult = await _memberManager.CreateAsync(identityUser, dto.Password);
        if (!createResult.Succeeded)
            return BadRequest(new { ok = false, errors = createResult.Errors.Select(e => e.Description) });

        // Sätt Name + ev defaultinställningar på IMember
        var m = _memberService.GetByKey(identityUser.Key);
        if (m is not null)
        {
            if (!string.IsNullOrWhiteSpace(dto.Name)) m.Name = dto.Name!;
            // Defaultvärden om du vill:
            m.SetValue(Alias_DefaultReminder, "08:00");
            _memberService.Save(m);
        }

        // Autologga in (OBS: använd PasswordSignInAsync)
        var signIn = await _signInManager.PasswordSignInAsync(username, dto.Password, isPersistent: true, lockoutOnFailure: false);
        if (!signIn.Succeeded) return Ok(new { ok = true, username, autoSignedIn = false });

        return Ok(new { ok = true, username, autoSignedIn = true });
    }

    // ---------- Login ----------
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        // tillåt e-post eller användarnamn
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
        var current = await _memberManager.GetCurrentMemberAsync();
        if (current is null) return Unauthorized();

        var m = _memberService.GetByKey(current.Key) ?? throw new InvalidOperationException("Member not found.");

        var dto = new ProfileDto(
            Email: m.Email ?? "",
            Name:  m.Name ?? "",
            LargerText:           m.GetValue<bool?>(Alias_LargerText)        ?? false,
            HighContrast:         m.GetValue<bool?>(Alias_HighContrast)      ?? false,
            LightMode:            m.GetValue<bool?>(Alias_LightMode)         ?? false,
            CaptionsOnByDefault:  m.GetValue<bool?>(Alias_CaptionsDefault)   ?? false,
            RemindersEnabled:     m.GetValue<bool?>(Alias_RemindersEnabled)  ?? false,
            DefaultReminderTime:  m.GetValue<string>(Alias_DefaultReminder)  ?? "08:00"
        );

        return Ok(dto);
    }

    // ---------- Me (PUT) ----------
    [HttpPut("me")]
    public async Task<IActionResult> UpdateMe([FromBody] ProfileDto dto)
    {
        var current = await _memberManager.GetCurrentMemberAsync();
        if (current is null) return Unauthorized();

        var m = _memberService.GetByKey(current.Key) ?? throw new InvalidOperationException("Member not found.");

        if (!string.IsNullOrWhiteSpace(dto.Name))  m.Name  = dto.Name;
        if (!string.IsNullOrWhiteSpace(dto.Email)) m.Email = dto.Email;

        m.SetValue(Alias_LargerText,       dto.LargerText);
        m.SetValue(Alias_HighContrast,     dto.HighContrast);
        m.SetValue(Alias_LightMode,        dto.LightMode);
        m.SetValue(Alias_CaptionsDefault,  dto.CaptionsOnByDefault);
        m.SetValue(Alias_RemindersEnabled, dto.RemindersEnabled);
        m.SetValue(Alias_DefaultReminder,  string.IsNullOrWhiteSpace(dto.DefaultReminderTime) ? "08:00" : dto.DefaultReminderTime);

        _memberService.Save(m);

        return Ok(new { ok = true });
    }

    // ---------- Byt lösen ----------
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        var current = await _memberManager.GetCurrentMemberAsync();
        if (current is null) return Unauthorized();

        var result = await _memberManager.ChangePasswordAsync(current, dto.CurrentPassword, dto.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { ok = false, errors = result.Errors.Select(e => e.Description) });

        // uppdatera autentisering/cookies
        await _signInManager.SignInAsync(current, isPersistent: true);

        return Ok(new { ok = true });
    }
}
