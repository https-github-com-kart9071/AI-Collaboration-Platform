using AICollaborationPlatform.App.Features.Code;

namespace AICollaborationPlatform.Tests;

public class CodeConnectionViewModelTests
{
    [Fact]
    public void Offers_every_provider_with_github_picked_first()
    {
        var dialog = new CodeConnectionViewModel();

        Assert.Equal(
            [CodeHostKind.GitHub, CodeHostKind.GitLab, CodeHostKind.Bitbucket, CodeHostKind.AzureDevOps, CodeHostKind.Other],
            dialog.Hosts.Select(h => h.Kind));
        Assert.Equal(CodeHostKind.GitHub, dialog.SelectedHost.Kind);
        Assert.All(dialog.Hosts, h => Assert.False(string.IsNullOrWhiteSpace(h.Description)));
    }

    [Fact]
    public void Picking_a_provider_changes_the_choice()
    {
        var dialog = new CodeConnectionViewModel();
        var changed = new List<string?>();
        dialog.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dialog.SelectedHost = dialog.Hosts.Single(h => h.Kind == CodeHostKind.Other);

        Assert.Equal(CodeHostKind.Other, dialog.SelectedHost.Kind);
        Assert.Contains(nameof(CodeConnectionViewModel.SelectedHost), changed);
    }

    [Fact]
    public void A_cleared_selection_keeps_the_last_choice()
    {
        var dialog = new CodeConnectionViewModel();
        dialog.SelectedHost = dialog.Hosts.Single(h => h.Kind == CodeHostKind.GitLab);

        dialog.SelectedHost = null!; // what a list box does for a moment when its items refresh

        Assert.Equal(CodeHostKind.GitLab, dialog.SelectedHost.Kind);
    }

    [Fact]
    public void Cloud_is_the_default_and_needs_no_address()
    {
        var dialog = new CodeConnectionViewModel();

        Assert.True(dialog.IsCloud);
        Assert.False(dialog.ShowServerAddress);

        dialog.ContinueCommand.Execute(null);

        Assert.Equal("https://github.com/", dialog.Server?.AbsoluteUri);
        Assert.True(dialog.IsSigningIn);
    }

    [Fact]
    public void Self_hosted_asks_for_the_server_address()
    {
        var dialog = new CodeConnectionViewModel { SelectedHost = CodeHostOption.All[1] }; // GitLab

        dialog.IsSelfHosted = true;

        Assert.False(dialog.IsCloud);
        Assert.True(dialog.ShowServerAddress);
        Assert.Equal("Self-managed GitLab address", dialog.ServerLabel);
        Assert.Equal("For example: https://gitlab.mycompany.com", dialog.ServerExample);
    }

    [Fact]
    public void Other_always_asks_for_a_repository_url()
    {
        var dialog = new CodeConnectionViewModel();

        dialog.SelectedHost = dialog.Hosts.Single(h => h.Kind == CodeHostKind.Other);

        Assert.False(dialog.HasCloud);
        Assert.True(dialog.ShowServerAddress);
        Assert.Equal("Repository URL", dialog.ServerLabel);
    }

    [Fact]
    public void Continue_shows_the_cleaned_address_it_will_use()
    {
        var dialog = new CodeConnectionViewModel { SelectedHost = CodeHostOption.All[1], IsSelfHosted = true };
        dialog.ServerAddress = " gitlab.mycompany.com/team/app ";

        dialog.ContinueCommand.Execute(null);

        Assert.Null(dialog.ServerAddressError);
        Assert.Equal("https://gitlab.mycompany.com", dialog.ServerAddress);
        Assert.Equal("https://gitlab.mycompany.com/", dialog.Server?.AbsoluteUri);
        Assert.True(dialog.IsSigningIn);
        Assert.Equal("https://gitlab.mycompany.com", dialog.SignInServer);
    }

    [Fact]
    public void Continue_with_a_bad_address_shows_why_and_editing_clears_it()
    {
        var dialog = new CodeConnectionViewModel { SelectedHost = CodeHostOption.All[1], IsSelfHosted = true };
        dialog.ServerAddress = "http://gitlab.mycompany.com";

        dialog.ContinueCommand.Execute(null);

        Assert.Equal("The address must start with https://", dialog.ServerAddressError);
        Assert.Null(dialog.Server);
        Assert.False(dialog.IsSigningIn); // stays on the first step

        dialog.ServerAddress = "https://gitlab.mycompany.com";

        Assert.Null(dialog.ServerAddressError);
    }

    // ---- Sign-in step ----

    private static CodeConnectionViewModel AtSignIn(CodeHostKind kind, string? server = null)
    {
        var dialog = new CodeConnectionViewModel { SelectedHost = CodeHostOption.All.Single(h => h.Kind == kind) };
        if (server is not null)
        {
            dialog.IsSelfHosted = true;
            dialog.ServerAddress = server;
        }

        dialog.ContinueCommand.Execute(null);
        Assert.True(dialog.IsSigningIn);
        return dialog;
    }

    [Fact]
    public void Github_cloud_asks_for_a_token_only_and_links_to_github()
    {
        var dialog = AtSignIn(CodeHostKind.GitHub);

        Assert.Equal("Sign in to GitHub", dialog.SignInTitle);
        Assert.Equal("Personal access token", dialog.SignIn!.TokenLabel);
        Assert.False(dialog.ShowUsername);
        Assert.Equal("https://github.com/settings/personal-access-tokens/new", dialog.SignIn.CreateTokenUrl);
        Assert.Contains("read-only", dialog.SignIn.Permissions);
    }

    [Fact]
    public void Self_hosted_links_to_the_token_page_on_that_server()
    {
        Assert.Equal("https://gitlab.acme.com/-/user_settings/personal_access_tokens",
            AtSignIn(CodeHostKind.GitLab, "gitlab.acme.com").SignIn!.CreateTokenUrl);
        Assert.Equal("https://github.acme.com/settings/tokens/new",
            AtSignIn(CodeHostKind.GitHub, "github.acme.com").SignIn!.CreateTokenUrl);
        Assert.Equal("https://acme.com/bitbucket/plugins/servlet/access-tokens/manage",
            AtSignIn(CodeHostKind.Bitbucket, "acme.com/bitbucket").SignIn!.CreateTokenUrl);
        Assert.Equal("https://devops.acme.com/tfs/DefaultCollection/_usersSettings/tokens",
            AtSignIn(CodeHostKind.AzureDevOps, "devops.acme.com/tfs/DefaultCollection").SignIn!.CreateTokenUrl);
    }

    [Fact]
    public void Gitlab_explains_the_read_only_scopes()
    {
        var signIn = AtSignIn(CodeHostKind.GitLab).SignIn!;

        Assert.Contains("read_api", signIn.Permissions);
        Assert.Contains("read_repository", signIn.Permissions);
    }

    [Fact]
    public void Bitbucket_cloud_and_other_also_ask_for_a_username()
    {
        var bitbucket = AtSignIn(CodeHostKind.Bitbucket);
        Assert.True(bitbucket.ShowUsername);
        Assert.Equal("Atlassian account email", bitbucket.SignIn!.UsernameLabel);
        Assert.Equal("API token", bitbucket.SignIn.TokenLabel);

        var other = AtSignIn(CodeHostKind.Other, "https://git.acme.com/team/app.git");
        Assert.True(other.ShowUsername);
        Assert.Equal("Username", other.SignIn!.UsernameLabel);
        Assert.Null(other.SignIn.CreateTokenUrl); // no single page to send people to
    }

    [Fact]
    public void Sign_in_with_empty_fields_says_what_is_missing_and_typing_clears_it()
    {
        var dialog = AtSignIn(CodeHostKind.Bitbucket);

        dialog.SignInToHostCommand.Execute(null);

        Assert.Equal("Atlassian account email is required.", dialog.UsernameError);
        Assert.Equal("API token is required.", dialog.TokenError);
        Assert.Equal("", dialog.StatusMessage);

        dialog.Username = "alex@acme.com";
        dialog.Token = "secret";

        Assert.Null(dialog.UsernameError);
        Assert.Null(dialog.TokenError);
    }

    [Fact]
    public void Sign_in_with_a_token_moves_on()
    {
        var dialog = AtSignIn(CodeHostKind.GitLab);
        dialog.Token = "  glpat-123  ";

        dialog.SignInToHostCommand.Execute(null);

        Assert.Null(dialog.TokenError);
        Assert.Null(dialog.UsernameError); // GitLab needs no username
        Assert.Contains("GitLab", dialog.StatusMessage);
        Assert.DoesNotContain("glpat", dialog.StatusMessage); // the token is never shown back
    }

    [Fact]
    public void Back_returns_to_the_provider_and_keeps_what_was_typed()
    {
        var dialog = AtSignIn(CodeHostKind.GitLab, "gitlab.acme.com");
        dialog.Token = "glpat-123";

        dialog.BackCommand.Execute(null);

        Assert.False(dialog.IsSigningIn);
        Assert.True(dialog.IsChoosingHost);
        Assert.Equal("https://gitlab.acme.com", dialog.ServerAddress);
        Assert.Equal("glpat-123", dialog.Token);
    }

    [Fact]
    public void The_token_is_hidden_until_shown()
    {
        var dialog = AtSignIn(CodeHostKind.GitHub);

        Assert.False(dialog.IsTokenVisible);
        dialog.ToggleTokenVisibilityCommand.Execute(null);
        Assert.True(dialog.IsTokenVisible);
    }

    [Fact]
    public void Cancel_closes_without_connecting()
    {
        var dialog = new CodeConnectionViewModel();
        bool? result = null;
        dialog.RequestClose += (_, connected) => result = connected;

        dialog.CancelCommand.Execute(null);

        Assert.False(result);
    }
}
