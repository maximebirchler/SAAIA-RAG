namespace SAAIA.Client.ToolAgent.Tests;

// Unit fixtures are checked in and copied beside the assembly. Historical paid
// campaigns keep their own archive paths and hashes; these helpers do not run them.
internal static class PortableContractFixtures
{
    internal const string MultiturnReplaySha256 =
        "B2D5C7E0418A798062F592C1F3D70E1F52D3C8B9A5348D21F970193685C491CB";

    internal static string DirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "PortableContracts");

    internal static string PathFor(string name) => Path.Combine(DirectoryPath, name);
}
