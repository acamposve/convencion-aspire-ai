using System;
using Convenciones.Administration;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

/// <summary>The account aggregate's own rules, without a database.</summary>
/// <remarks>
/// Capability checks are exercised through the API instead (the self-lockout guard reads the granted
/// roles), because building a role graph by hand would test the test's plumbing rather than the rule.
/// </remarks>
public sealed class AdministrationDomainTests
{
    private static readonly Guid Actor = new("c1000000-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_account_requires_a_subject_an_email_and_a_display_name()
    {
        Should.Throw<AdministrationRuleException>(() =>
            UserAccount.Create(Guid.Empty, "user@example.test", "User", Actor, Now));

        Should.Throw<AdministrationRuleException>(() =>
            UserAccount.Create(Guid.NewGuid(), "not-an-email", "User", Actor, Now));

        Should.Throw<AdministrationRuleException>(() =>
            UserAccount.Create(Guid.NewGuid(), "user@example.test", "  ", Actor, Now));
    }

    [Fact]
    public void A_new_account_starts_unblocked_with_an_empty_profile_and_no_roles()
    {
        var user = UserAccount.Create(Guid.NewGuid(), "user@example.test", "User", Actor, Now);

        user.IsBlocked.ShouldBeFalse();
        user.ExpiresOn.ShouldBeNull();
        user.Profile.ShouldNotBeNull();
        user.Profile!.Phone.ShouldBeNull();
        user.RoleKeys.ShouldBeEmpty();
        user.CreatedBy.ShouldBe(Actor);
    }

    [Fact]
    public void Replacing_roles_adds_and_removes_and_records_the_granting_actor()
    {
        var user = UserAccount.Create(Guid.NewGuid(), "user@example.test", "User", Actor, Now);
        var editor = Guid.NewGuid();
        var negotiator = Guid.NewGuid();

        user.ReplaceRoles([negotiator, editor], Actor, Now);

        user.Roles.Count.ShouldBe(2);
        user.Roles.ShouldAllBe(assignment => assignment.GrantedBy == Actor);

        user.ReplaceRoles([editor], Actor, Now);

        user.Roles.Count.ShouldBe(1);
        user.Roles[0].RoleId.ShouldBe(editor);
    }

    [Fact]
    public void Renaming_and_changing_the_email_reject_blank_values()
    {
        var user = UserAccount.Create(Guid.NewGuid(), "user@example.test", "User", Actor, Now);

        Should.Throw<AdministrationRuleException>(() => user.Rename("  ", Actor, Now));
        Should.Throw<AdministrationRuleException>(() => user.ChangeEmail("not-an-email", Actor, Now));

        user.Rename("Renamed", Actor, Now);
        user.ChangeEmail("renamed@example.test", Actor, Now);

        user.DisplayName.ShouldBe("Renamed");
        user.Email.ShouldBe("renamed@example.test");
        user.UpdatedBy.ShouldBe(Actor);
    }

    [Fact]
    public void Setting_the_profile_replaces_phone_and_company()
    {
        var user = UserAccount.Create(Guid.NewGuid(), "user@example.test", "User", Actor, Now);
        var company = Guid.NewGuid();

        user.SetProfile("+58 412 000 0000", company, Actor, Now);

        user.Profile!.Phone.ShouldBe("+58 412 000 0000");
        user.Profile.CompanyId.ShouldBe(company);

        user.SetProfile(null, null, Actor, Now);

        user.Profile.Phone.ShouldBeNull();
        user.Profile.CompanyId.ShouldBeNull();
    }
}
