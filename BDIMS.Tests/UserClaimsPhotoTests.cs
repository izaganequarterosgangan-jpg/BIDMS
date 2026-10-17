using System.Collections.Generic;
using System.Security.Claims;
using BDIMS.Models;
using BDIMS.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BDIMS.Tests;

/// <summary>
/// Pins the claim-to-badge mapping the photo upload depends on. The profile card, the
/// sidebar badge and the two header badges all resolve their image source through
/// UserClaims, so a change to the claim name or the serving route would otherwise only
/// surface as a broken image at runtime.
/// </summary>
public class UserClaimsPhotoTests
{
    private static ClaimsPrincipal PrincipalWith(string? fileName)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Isagane Quarteros")
        };

        if (fileName is not null)
        {
            claims.Add(new Claim(UserAccount.ProfilePhotoClaim, fileName));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
    }

    [Fact]
    public void GetPhotoUrl_ReturnsTheServingRouteForTheStoredFileName()
    {
        var principal = PrincipalWith("user-photo-3-20260101120000000.png");

        var url = UserClaims.GetPhotoUrl(principal);

        Assert.NotNull(url);
        Assert.Contains("/Account/ProfilePhoto", url);
        Assert.Contains("user-photo-3-20260101120000000.png", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetPhotoUrl_IsNullWhenNoPhotoHasBeenUploaded(string? stored)
    {
        // Null, not empty and not a broken URL: the views test this to decide between
        // rendering the image and falling back to the initials.
        Assert.Null(UserClaims.GetPhotoUrl(PrincipalWith(stored)));
    }

    [Fact]
    public void BuildPhotoUrl_ProducesTheSameRouteAsTheClaimBasedLookup()
    {
        // The Profile page builds its URL from the account row while the layout builds
        // it from the claim; the two must not diverge.
        var stored = "user-photo-9-20260101120000000.jpg";

        Assert.Equal(
            UserClaims.GetPhotoUrl(PrincipalWith(stored)),
            UserClaims.BuildPhotoUrl(stored));
    }

    [Fact]
    public void ProfileViewModel_ProjectsThePhotoUrlAndKeepsItOutOfBinding()
    {
        var account = new UserAccount
        {
            Id = 3,
            Email = "staff@barangay.gov.ph",
            DisplayName = "Isagane Quarteros",
            FirstName = "Isagane",
            LastName = "Quarteros",
            Role = "Barangay Secretary",
            ProfilePhoto = "user-photo-3-20260101120000000.png"
        };

        var model = ProfileViewModel.FromAccount(account);

        Assert.Contains("/Account/ProfilePhoto", model.PhotoUrl);

        // The upload is its own endpoint; a posted profile form must never be able to
        // set the photo by adding a field to the request.
        Assert.NotEmpty(typeof(ProfileViewModel)
            .GetProperty(nameof(ProfileViewModel.PhotoUrl))!
            .GetCustomAttributes(typeof(BindNeverAttribute), inherit: true));
    }

    [Fact]
    public void ProfileViewModel_HasAnEmptyPhotoUrlForAnAccountWithoutOne()
    {
        var account = new UserAccount
        {
            Email = "staff@barangay.gov.ph",
            DisplayName = "Isagane Quarteros",
            FirstName = "Isagane",
            LastName = "Quarteros",
            Role = "Barangay Secretary"
        };

        // Empty rather than null so the view's IsNullOrEmpty check falls back to the
        // initials, keeping accounts created before this feature rendering unchanged.
        Assert.Equal(string.Empty, ProfileViewModel.FromAccount(account).PhotoUrl);
    }
}