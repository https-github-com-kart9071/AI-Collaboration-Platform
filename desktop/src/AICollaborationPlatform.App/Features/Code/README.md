# Code feature (planned)

Connect GitHub, GitLab or any Git repository, download it, and explore its files like in VS Code (Stories 8–16).

Planned layout, matching `Features/Jira`:

```
Features/Code/
├── CodeView.xaml, CodeViewModel.cs   The Code page
├── CodeResources.xaml                Code-only visuals (file icons, tree styles)
├── Connection/   Connect to a host: ICodeHostClient + GitHub / GitLab / Bitbucket / Azure DevOps clients, tokens
├── Repositories/ List and pick repositories, add by URL
├── Git/          Clone, refresh, branches, history (LibGit2Sharp)
├── Explorer/     Folder tree
├── Viewer/       Code viewer, tabs, syntax highlighting
└── Search/       Quick open (Ctrl+P), search in files (Ctrl+Shift+F)
```

Namespace: `AICollaborationPlatform.App.Features.Code`. Tests go in `tests/AICollaborationPlatform.Tests/Features/Code/`.
Tokens go in Windows Credential Manager through `Core/Storage/ICredentialStore`, never in settings or logs.
