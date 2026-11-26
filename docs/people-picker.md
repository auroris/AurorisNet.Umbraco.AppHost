# People picker

The **People picker** (`appPeoplePicker`) is a form companion supplied by AppHost. Add it inside a form on a Website page and bind its identity and name settings to writable text fields. It places a search button beside the name field and opens a separate search window.

## Configuration and access

The published block configures the search OU (or explicitly permits the whole domain), child-OU recursion, search/returned LDAP attributes, account filters, and optional member/backoffice group allowlists. Empty group lists admit anyone who can access the page; otherwise either group list admits the caller (no linked Windows SID required). Settings apply to both search and selection and cannot be overridden via query parameters.

- **Attribute mapping:** one `attribute=formField` pair per line. Each picker owns distinct writable text fields. Optional `mappedField=readOnlyMirrorField` allows manual fallback when the directory returns no value (the form resource must load the mirror with the directory value). Compact-uppercase fields strip whitespace and uppercase input; manual entry can be disabled per picker.
- **Prefill editable fields:** e.g. `mail=contactEmail` fills the address on selection, clears it if the selected person has no directory email, and saves requester edits.

## Endpoints, validation, and caching

- `GET /api/people-picker/{page}/{block}?s=...` searches; `?sid=...` validates a selection within the same scope (responses are non-cacheable).
- On save, AppBlocks' `IFormSubmissionProcessor` runs `PeoplePickerService` again for every new or changed SID. Unchanged saved identities keep stored directory-owned fields during outages; search results never authorize a new saved identity.
- Optional cached selection (off by default) allows recent validated people during outages (isolated per page/picker/settings, 1–1440 minute TTL, 2,000-person process cap, cleared on restart or live rejection).
- LDAP calls use the Windows process identity with bounded result counts and 1–15s timeouts; search terms and person records are not logged.

## Extending and testing

Shared types live in `UmbracoAppHost.PeoplePicker`:
- `IPeoplePickerSelectionObserver` — persist canonical contact data after save-time validation.
- `PeoplePickerService.WasValidated` — verify a changed SID passed the form processor.
- `IPeoplePickerDirectory` — replaceable directory boundary for tests.

```powershell
dotnet test --project tests/UmbracoAppHost.Tests/UmbracoAppHost.Tests.csproj --filter-class UmbracoAppHost.Tests.PeoplePickerTests --no-progress
node --test tests/ui/people-picker.browser.test.mjs
```

Browser tests use Playwright from sibling `AurorisNet.InteractiveReports` (override with `INTERACTIVE_REPORTS_ROOT` and `BROWSER_PATH`).
