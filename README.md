# Creche Manager

A Windows desktop application for running a crèche (daycare / nursery): children, parents, enrolment,
daily attendance, monthly fees, payments, staff and expenses.

It runs **fully offline on a single PC**: no server, no internet connection, no cloud. All data is one local
SQLite file.

> The name "Creche Manager" is a placeholder. To rename the product, change `Product`/`Company` in
> `Directory.Build.props` and the two constants in `src/Creche.Application/Settings/Options.cs`
> (`AppInfo.Name` is the display name, `AppInfo.FolderName` the data-folder name).

## Quick start

Requirements for development: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src/Creche.App
```

First start:

1. The database is created automatically and brought up to date (no manual step).
2. Sign in with user **`admin`** and password **`admin`**. The login screen says so until you have done it.
3. You are immediately asked to choose your own password (minimum 6 characters).

To try the application with sample data (3 groups, 9 children, parents, 4 fee plans, 5 employees), set
`"SeedDemoData": true` in `src/Creche.App/appsettings.json` **before the first start**. Demo data is only
inserted into an empty database. The admin account is always created, with or without demo data.

Useful keys: **Ctrl+K** global search, **F5** reload the current screen, **Esc** close a dialog.

## Where the data lives

Everything is stored under `%LOCALAPPDATA%\CrecheManager\`:

| Path | Content |
| --- | --- |
| `creche.db` (+ `-wal`, `-shm`) | The SQLite database (WAL journal mode) |
| `images\` | Child photos and the logo, resized on import |
| `documents\` | Files attached to children |
| `backups\` | Automatic, manual and "before restore" backups |
| `logs\` | One log file per day (`creche-yyyyMMdd.log`), kept 30 days |
| `ui-state.json` | Remembered filters, sort orders, view choices, theme, language, window size |

To keep the data somewhere else, edit `appsettings.json` next to the exe:

```json
{
  "Storage":  { "DataFolder": "D:\\CrecheData" },
  "Database": { "SeedDemoData": false }
}
```

### Backups

- **Automatic**: once a day (at startup, then checked hourly while the app stays open). The number of
  automatic backups kept is set in Settings → Backup (default 14).
- **Manual**: Settings → Backup → *Back up now*.
- **Restore**: pick a backup from the list or from a file. A safety copy of the current data is always taken
  first (`creche-before-restore-…db`), the file is checked to be a valid backup of this application, then the
  app restarts.

Backups use the SQLite online-backup API, so they are consistent even while the app is in use. They never
leave the computer: copy the `backups` folder to a USB key regularly.

### Privacy

Children's medical and family data is sensitive. Nothing is readable without signing in, every service checks
the signed-in user's role, and no data is ever sent anywhere. Passwords are stored as PBKDF2-SHA256 hashes
(100,000 iterations, 16-byte random salt, 32-byte key, constant-time comparison).

The database file itself is **not encrypted**: anyone with access to the Windows account can copy it. Protect
the PC with a Windows password and, ideally, BitLocker.

## Solution structure

Clean Architecture, one solution (`Creche.sln`):

| Project | Role | References |
| --- | --- | --- |
| `src/Creche.Domain` | Entities, enums, business rules (fees, attendance, capacity, permissions) | nothing |
| `src/Creche.Application` | Service interfaces, DTOs, settings classes, money and CSV helpers | Domain |
| `src/Creche.Infrastructure` | EF Core `DbContext`, migrations, service implementations, `AddCrecheInfrastructure()` | Application |
| `src/Creche.App` | WPF: Views, ViewModels, Dialogs, Controls, Converters, Resources, platform services | Infrastructure |
| `tests/Creche.Tests` | xUnit tests on a real in-memory SQLite database | Infrastructure |

Stack: C# (latest), .NET 10, WPF, CommunityToolkit.Mvvm 8.4.2, EF Core 10 + SQLite (`AddDbContextFactory`),
Microsoft.Extensions.Hosting, xUnit 2.9.3. No UI, charting or cloud libraries: the dialog service, toasts,
icon control, responsive grid, meters, avatar and image upload are built in-house (`src/Creche.App/Controls`,
`src/Creche.App/Services`).

## Roles

Permissions are enforced in the service layer (`RolePermissions` in the Domain, `ICurrentUser.Demand(...)` in
every service), not only by hiding screens.

| Role | Can do |
| --- | --- |
| **Admin** | Everything, including staff, expenses, reports, users, fee plans, settings and backups |
| **Secretary** | Dashboard, children, parents, enrolment, fees and invoices, payments |
| **Educator** | Attendance and activity log, **for their own group only** |

Users are disabled, never deleted, and there is always at least one active admin.

## How the money works

- Every amount is a `decimal`, rounded to 2 places (half away from zero). Currency is a setting (default `DA`).
- **Fee plans**: full day, half day (monthly fee) or hourly (hours attended × rate), with or without a monthly
  meal supplement. A child can also have a special monthly fee.
- **Sibling discount**: among a family's enrolled children, the eldest pays full price; the 2nd and the 3rd+
  get their own percentage (settings). The discount applies to the childcare fee, not to meals or extras.
- **Extra charges** (late pick-up, meals, activities, transport, other) wait as "pending" and are put on the
  child's next monthly invoice. Late pick-up charges are created automatically at check-out from the closing
  time, grace period and block size in the settings.
- **Monthly invoices** are generated in one click (Fees & invoices → *Generate monthly invoices*): one invoice
  per billable child, never twice for the same month. Hourly plans bill the hours of that month, so generate
  them at month end.
- **Payments** are recorded per family (cash, card, other) and settle the oldest invoices first. A partial
  payment leaves the invoice "partly paid"; an overpayment is kept as credit and used automatically by the
  next invoices. A wrong payment is *voided* (it stays in the history), never deleted.
- **Due date**: the configured day of the billed month, or 7 days after the invoice is issued if that day
  has already passed.

Nothing is hard-deleted: children, parents and groups are archived, users and staff disabled, payments voided,
invoices cancelled, attendance records flagged as deleted.

## Migrations

Pending migrations are applied automatically at startup (`Database.MigrateAsync()`), so there is no manual
`database update` step, on a developer machine or at the crèche.

After changing an entity or the `DbContext`:

```powershell
./scripts/add-migration.ps1 AddSomething
```

This uses `dotnet-ef` as a local tool (`.config/dotnet-tools.json`) and the design-time factory in the
Infrastructure project.

## Tests

```powershell
dotnet test
# or
./scripts/test.ps1
```

98 tests cover fee calculation, sibling discount, partial payments and balances, attendance rules, group
capacity, role checks, soft delete, backup/restore and the CSV and money helpers. They run the real services
against a real SQLite database (in memory, schema created by the migrations) so transactions, foreign keys and
unique indexes are exercised. The backup tests use a temporary database file.

## Publishing

```powershell
./scripts/publish.ps1                 # folder build in ./publish
./scripts/publish.ps1 -SingleFile     # one CrecheManager.exe + appsettings.json
```

The script runs the tests first and stops if one fails, then publishes a **self-contained win-x64** build
with **ReadyToRun**, so the target PC does not need .NET installed. Copy the output folder to the PC and
start `CrecheManager.exe`.

## Printing and export

Receipts, invoices, enrolment forms, monthly attendance sheets, the list of families who owe money and
reports are built as `FlowDocument`s and go through WPF's own printing stack (`PrintDialog`,
`System.Printing`). Each has a preview with **Print** and **Export to PDF**; PDF export sends the document to
Windows' built-in *Microsoft Print to PDF* printer, which asks where to save the file. Lists and reports also
export to **CSV** (UTF-8 with BOM, `;` separator, opens directly in Excel).

## Languages and themes

English, French and Arabic, switchable at runtime (login screen or Settings → Appearance). Language files are
JSON, embedded in the exe (`src/Creche.App/Resources/Languages`); English is the fallback for a missing key.
Arabic switches the whole interface to right-to-left. To add a language, add `xx.json` and one line in
`Loc.Languages`.

Dark and light themes are XAML resource dictionaries (`Resources/Themes`), switchable at runtime. Fonts are
bundled as resources: Sora for text and JetBrains Mono for numbers (both SIL Open Font License, licences next
to the font files). Scripts they do not cover, such as Arabic, fall back to Segoe UI.

## Troubleshooting

- **The app does not start twice**: by design, one copy per machine (named mutex). Starting it again brings
  the running window to the front.
- **Something went wrong**: look at the newest file in `%LOCALAPPDATA%\CrecheManager\logs`. Broken XAML
  bindings are logged there too.
- **Forgot the admin password**: another admin can set a new one in Users. If there is no other admin,
  restore a backup taken when you still knew it.
