using AppBlocks.Setup;

namespace UmbracoAppHost.PeoplePicker;

/// <summary>Installs the shared picker on existing hosts as well as new installations.</summary>
public sealed class PeoplePickerMigrationPlan() : AppBlocksPackagePlan("AppHost.PeoplePicker", "AppHost People picker")
{
    protected override void DefinePlan() => To<InstallPackageDefinitions>("2026-11-17-people-picker");
}
