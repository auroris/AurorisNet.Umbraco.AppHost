# Release migrations

The first release has two complete installers. At 1.0.0, `1.0.0-Base` and `1.0.0-Current` must contain identical files. Freeze Base when 1.0.0 ships and tag the release so its dependency versions and application code are reproducible.

For each release after 1.0.0, follow these steps.

1. Rename the single `*-Current` folder to the new version and update its clean installer.
2. Add a `<from>-<to>` folder for the change to an installed site. Use the same component and dialect paths for its SQL. For a release with no installation changes, add a short README to the transition folder so Git retains it. Keep previously shipped transition folders unchanged.
3. Install Current on an empty site. Separately, start the previous release, add representative user data, upgrade it and restart it. Compare the release-owned schema, starting data and definitions reached by both routes. Verify that existing records and editor drafts survive. Check the frozen folders against the previous release tag.

The host snapshot contains its uSync export, people-picker definition and sample installer. The build exposes the export at `uSync/` for downstream sites and deployment tools. The startup code in `Runtime/` preserves the site's uSync working copy and applies changes to shared definitions under the existing import settings.

The sample database uses `Sample/Schema/<dialect>/` within a snapshot or transition. AppBlocks owns its attachment tables and block definitions. The package build targets the four AppBlocks 1.0.0 packages. Publish those packages before running AppHost’s public package build. Test this host with the matching AppBlocks build, then run the definition-installation and sample-database tests. Use the MSBuild property `InstallSnapshot=1.0.0-Base` to exercise the original installer.

For an upgrade to stored CMS content, implement the patch in the transition folder and register it in Current’s `SiteDefinitions.ContentUpgrades`. Startup records the initial installation and runs pending patches once, in order.
