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

The app is organised **by feature**: everything for one integration (its API client, storage, view models and views) sits in one folder under `Features/`. Shared pieces live in `Core`, `UI` and `Shell`.

```
desktop/
├── AICollaborationPlatform.slnx
├── src/AICollaborationPlatform.App/
│   ├── App.xaml(.cs)          Startup: logging, dependency injection, global error handling
│   │
│   ├── Shell/                 The main window and what ties features together
│   │   ├── MainWindow, MainViewModel, PageViewModel
│   │   ├── NavigationService, NavigationItem
│   │   ├── Dialogs/           IDialogService: opens windows so view models stay testable
│   │   └── Home/              Home page
│   │
│   ├── Core/                  App-wide, non-visual services (no feature code)
│   │   ├── Storage/           AppPaths, settings.json, Windows Credential Manager
│   │   └── Services/          BrowserLauncher
│   │
│   ├── UI/                    Shared WPF building blocks (no feature code)
│   │   ├── Themes/            DarkTheme.xaml: colours, styles, scrollbars, tables, tabs
│   │   ├── Controls/          Spinner
│   │   ├── Behaviors/         SmoothScroll
│   │   ├── Converters/        Visibility and format converters
│   │   └── Windows/           DarkTitleBar
│   │
│   └── Features/
│       ├── Jira/
│       │   ├── JiraView, JiraViewModel   The Jira page
│       │   ├── JiraResources.xaml        Jira-only visuals (status lozenges, icons, avatars)
│       │   ├── Connection/    Connect dialog, session, auth, URL checks
│       │   ├── Api/           REST client, retries, JSON/ADF parsing, Jira data types
│       │   ├── Sync/          Fetch/Refresh from Jira into SQLite
│       │   ├── Storage/       JiraDatabase (SQLite)
│       │   ├── Browse/        Jira data page: projects, issue table, issue panel
│       │   └── Editing/       Edit issues and add comments
│       └── Code/              (next) GitHub / GitLab repositories, see Features/Code/README.md
│
└── tests/AICollaborationPlatform.Tests/   xUnit tests, same folders as the app
    ├── Core/  Shell/  UI/
    ├── Features/Jira/{Api,Connection,Sync,Storage,Browse,Editing}/
    └── TestDoubles/           Shared fakes
```

**Rules that keep it tidy**

- **Namespaces follow the area, not every sub-folder:** `AICollaborationPlatform.App.Core`, `.UI`, `.Shell`, `.Features.Jira`. A feature's sub-folders only group its files.
- **`Core` and `UI` never reference a feature.** The one exception is `Core/Storage/AppSettings.cs`, the shape of `settings.json`, which holds each feature's saved section (e.g. `SavedJiraConnection`, owned by the Jira feature).
- **Features don't reference each other directly.** If two features need to talk (e.g. linking commits to Jira issues), add a small interface the other feature implements.
- **Adding a feature:**
  1. Create `Features/<Name>/`.
  2. Register its services and page in `App.ConfigureServices`.
  3. Map its view model to its view with a `DataTemplate` in `App.xaml`.
  4. Merge its `<Name>Resources.xaml` there, if it has one.
  5. Add tests under `tests/.../Features/<Name>/`.

### Key concepts (for learning C#/WPF)

| Concept | Where to look |
|---|---|
| **MVVM**: views bind to view models and have no logic in code-behind | `Shell/MainViewModel.cs`, `Shell/MainWindow.xaml` |
| **Commands**: `[RelayCommand]` generates `OpenJiraCommand` | `MainViewModel.OpenJira()` |
| **Observable properties**: `[ObservableProperty]` generates `StatusMessage` | `Features/Jira/Connection/JiraConnectionViewModel.cs` |
| **Dependency injection**: services are registered once and injected through constructors | `App.ConfigureServices` |
| **DataTemplates**: a view model type maps to its view | `App.xaml` |
| **Resource dictionaries**: the theme is split into shared and feature files | `UI/Themes/DarkTheme.xaml`, `Features/Jira/JiraResources.xaml` |
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

To try the app against other data (for example a test database) without touching your own, set `AICOLLAB_DATA_DIR` to another folder before starting it. Settings, logs and `data.db` are then read from and written to that folder:

```powershell
$env:AICOLLAB_DATA_DIR = "C:\temp\aicollab-test"; dotnet run --project src/AICollaborationPlatform.App
```

### Browsing Jira data

The **Jira data** page in the sidebar works offline from `data.db`:

- **Projects** on the left, **issues** in a table on the right (Key, Summary, Type, Status, Assignee, Priority, Created, Updated)
- Click a column header to sort, click again to reverse; keys sort naturally (KAN-9 before KAN-10)
- Filter by status, assignee and type; search key or summary (**Ctrl+F** jumps to the search box)
- Double-click an issue (or press **Enter**) for its description, comments, history and attachments; **Esc** closes it
- **Open in Jira** opens the issue in your browser
- The table is virtualized, so 10,000+ issues scroll and filter smoothly

## Roadmap

1. ✅ Project setup and app shell
2. ✅ Jira connection dialog (URL, auth type, token, Test Connection)
3. ✅ Store credentials securely (Windows Credential Manager)
4. ✅ Jira API client service
5. ✅ Fetch all Jira data into SQLite
6. ✅ Browse the fetched data in the UI
7. Export (CSV / Excel / JSON)
8. Windows installer
9. AI insights (Python backend)

## License

MIT. See [LICENSE](LICENSE).

