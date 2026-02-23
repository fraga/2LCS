# 2LCS TUI Migration - Feature Gap Analysis

**Date:** February 22, 2026
**Repo:** ~/src/repo/2LCS
**Branch:** master

## Migration Status

Build: **passing** (0 warnings, 0 errors)
Framework: **.NET 10**
JSON: **System.Text.Json** (Newtonsoft.Json fully removed)
UI: **Spectre.Console** TUI (WinForms removed)
Auth: **Microsoft.Playwright** (browser cookie capture with MFA support)

---

## Implemented in TUI

### Authentication
- [x] Use saved cookie
- [x] Login with browser (Playwright)
- [x] Paste cookie
- [x] Edit LCS endpoints
- [x] Re-authenticate (browser or cookie)

### Project Management
- [x] Select project
- [x] Project users list

### Environment Management
- [x] Show environments (CHE + SaaS)
- [x] Refresh environments
- [x] Start environment
- [x] Stop environment
- [x] Deallocate environment
- [x] Delete environment
- [x] Show credentials
- [x] Show RDP details
- [x] Show ongoing action
- [x] Open details in browser
- [x] Upcoming updates

### Session
- [x] Session settings (re-auth, replace cookie, edit endpoints)

---

## Missing from TUI

### Environment Operations (High Priority)
- [ ] Add NSG firewall rule (`SaasAddNsgRule`)
- [ ] Delete NSG firewall rule (`SaasDeleteNsgRule`)
- [ ] Deploy package / Apply hotfix (`DeployPackageToolStripMenuItem`)
- [ ] Restart service (`SaasRestartService`)
- [ ] Open RDP connection directly (`OpenRDPConnectionToolStripMenuItem`, `SaasOpenRdpConnectionToolStripMenuItem`)
- [ ] Logon to application URL (`LogonToApplicationToolStripMenuItem`)

### Environment Information (Medium Priority)
- [ ] Detailed build info (`DetailedBuildInfoToolStripMenuItem`)
- [ ] Detailed version information (`DetailedVersionInformationToolStripMenuItem`)
- [ ] Environment change history (`EnvironmentChangeHistoryToolStripMenuItem`)
- [ ] Environment monitoring (`EnvironmentMonitoringToolStripMenuItem`)
- [ ] Data packages history (`DataPackagesHistoryToolStripMenuItem`)
- [ ] System diagnostics (`SystemDiagnosticsToolStripMenuItem`)

### Hotfix Search (Medium Priority)
- [ ] CHE metadata hotfixes
- [ ] CHE application binary hotfixes
- [ ] CHE platform hotfixes
- [ ] SaaS application metadata hotfixes
- [ ] SaaS application binary hotfixes
- [ ] SaaS platform binary hotfixes
- [ ] SaaS critical metadata hotfixes

### Export Features (Medium Priority)
- [ ] Export to RDCMan (.rdg) connections
- [ ] Export to Remote Desktop Manager connections
- [ ] Export passwords as PowerShell script
- [ ] Export project data (CSV)
- [ ] Export NuGet package list
- [ ] Export all instances across projects
- [ ] Export environment changes (all/CHE/SaaS/cloud)

### Project-Level Features (Lower Priority)
- [ ] Asset Library browser
- [ ] Custom links
- [ ] Work items / Service requests
- [ ] Subscription estimator
- [ ] Subscriptions available
- [ ] Project settings
- [ ] Support issues

### Retail-Specific (Lower Priority)
- [ ] Logon to POS
- [ ] Retail Storefront URL
- [ ] Retail Server URL

---

## Modernization Completed

### Packages Removed
- `Newtonsoft.Json` -- replaced with `System.Text.Json` (~60 call sites)
- `System.Data.DataSetExtensions` -- built into .NET 10 runtime
- `Microsoft.CSharp` -- unnecessary on .NET 10
- `System.Runtime.CompilerServices.Unsafe` -- unnecessary on .NET 10

### Packages Upgraded to 10.0.0
- `System.Configuration.ConfigurationManager`
- `System.Drawing.Common`
- `System.Runtime.Caching`
- `System.Resources.Extensions`

### Dead Code Removed
- `WebBrowserHelper.cs` -- stripped to just `OpenUri()` (IE registry hacks removed)
- `ServicePointManager.SecurityProtocol = Tls12` -- obsolete on .NET 10

### Current Package List
| Package | Version | Notes |
|---------|---------|-------|
| AutoMapper | 13.0.1 | Third-party, latest |
| CsvHelper | 33.0.1 | Third-party, latest |
| DocX | 3.0.23523.1209 | Third-party |
| HtmlAgilityPack | 1.11.72 | Third-party, latest |
| Microsoft.Playwright | 1.58.0 | New (TUI auth) |
| Spectre.Console | 0.49.1 | New (TUI framework) |
| System.Configuration.ConfigurationManager | 10.0.0 | .NET 10 |
| System.Drawing.Common | 10.0.0 | .NET 10 |
| System.Linq.Dynamic.Core | 1.6.1 | Third-party |
| System.Resources.Extensions | 10.0.0 | .NET 10 |
| System.Runtime.Caching | 10.0.0 | .NET 10 |

---

## Recommended Next Steps

1. **Smoke test** with real LCS cookie to validate System.Text.Json migration
2. **NSG firewall rules** -- most commonly used missing feature
3. **Deploy package** -- core workflow for environment management
4. **Export to RDCMan/RDM** -- high value for ops teams
5. **Hotfix search** -- needed for patch management
6. Write unit tests for JSON deserialization (Response.Data patterns)
