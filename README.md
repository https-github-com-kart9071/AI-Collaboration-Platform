# AI Collaboration Platform

A Windows desktop app that connects to your work tools (starting with Jira), brings their data together, and analyzes it with AI.

## Architecture

```
ai-collaboration-platform/
├── desktop/   C# / .NET 10 WPF app. The Windows UI and OS integration.  (Story 1 ✅)
└── backend/   Python (FastAPI) service for AI, crawling and data work.   (planned)
```

- **Desktop (C#)** handles the UI, Windows Credential Manager, notifications, file dialogs and installer.
- **Backend (Python)** handles AI model calls, web crawling and heavy data processing, where Python's libraries are strongest. The desktop app talks to it over local HTTP (`http://127.0.0.1:<port>`).

Jira data is fetched through the official **Jira REST API**, not by scraping the website.

## Desktop app

### Prerequisites

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Optional: Visual Studio 2022+ (with the ".NET desktop development" workload), or VS Code with the C# Dev Kit

### Run

```bash
cd desktop
dotnet run --project src/AICollaborationPlatform.App
```

Or open `desktop/AICollaborationPlatform.slnx` in Visual Studio and press **F5**.

### Test

```bash
cd desktop
dotnet test
```

### Project layout

```
desktop/
├── AICollaborationPlatform.slnx
├── src/AICollaborationPlatform.App/
│   ├── App.xaml(.cs)      Startup: logging, dependency injection, global error handling, theme
│   ├── Views/             XAML windows and pages (MainWindow, HomeView, JiraView)
│   ├── ViewModels/        MVVM view models, one per page (CommunityToolkit.Mvvm)
│   ├── Services/          App services (navigation now; Jira client in Story 4)
│   ├── Models/            Plain data types
│   └── Storage/           Local paths, settings and (later) the SQLite database
└── tests/AICollaborationPlatform.Tests/   xUnit tests
```

### Key concepts (for learning C#/WPF)

| Concept | Where to look |
|---|---|
| **MVVM**: views bind to view models and have no logic in code-behind | `ViewModels/MainViewModel.cs`, `Views/MainWindow.xaml` |
| **Commands**: `[RelayCommand]` generates `OpenJiraCommand` | `MainViewModel.OpenJira()` |
| **Observable properties**: `[ObservableProperty]` generates `ConnectionStatus` | `ViewModels/JiraViewModel.cs` |
| **Dependency injection**: services are registered once and injected through constructors | `App.ConfigureServices` |
| **DataTemplates**: a view model type maps to its view | `App.xaml` |
| **Structured logging**: Serilog through `ILogger<T>` | `App.OnStartup`, `NavigationService` |

### Logs

Logs are written to `%APPDATA%\AICollaborationPlatform\logs\`, one file per day (`app20261030.log`), and kept for 14 days.

### Where your data is stored

| What | Where |
|---|---|
| Jira URL, Cloud/Server choice, email | `%APPDATA%\AICollaborationPlatform\settings.json` (plain JSON, no secrets) |
| Jira API token / Personal Access Token | **Windows Credential Manager**, entry `AICollaborationPlatform/Jira`, encrypted by Windows for your user account |
| Fetched Jira data (projects, issues, comments, attachment details, history, users, boards, sprints) | `%APPDATA%\AICollaborationPlatform\data.db` (SQLite; open it with any SQLite viewer, e.g. DB Browser for SQLite) |

To see or delete the token yourself, open **Control Panel → Credential Manager → Windows Credentials**. **Disconnect** in the app removes both.

## Roadmap

1. ✅ Project setup and app shell
2. ✅ Jira connection dialog (URL, auth type, token, Test Connection)
3. ✅ Store credentials securely (Windows Credential Manager)
4. ✅ Jira API client service
5. ✅ Fetch all Jira data into SQLite
6. Browse the fetched data in the UI
7. Export (CSV / Excel / JSON)
8. Windows installer
9. AI insights (Python backend)

## License

MIT. See [LICENSE](LICENSE).

