using System.Reflection;
using Xunit;

namespace SAAIA.Testing;

public sealed class TestProfileCoverageTests
{
    [Fact]
    public void Every_live_test_declares_its_profile_and_cannot_be_enabled_by_a_filename()
    {
        var failures = new List<string>();
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(static type => type.Name.StartsWith("Live", StringComparison.Ordinal)))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var fact = method.GetCustomAttributesData().FirstOrDefault(static attribute => typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType));
            if (fact is null) continue;
            var profiles = ProfileTraits(type.GetCustomAttributesData().Concat(method.GetCustomAttributesData()));
            if (profiles.Count != 1) failures.Add($"{type.Name}.{method.Name}: expected one explicit Profile trait");
            else if (profiles[0] != "Unit" && fact.AttributeType != typeof(ProfileFactAttribute) && fact.AttributeType != typeof(ProfileTheoryAttribute))
                failures.Add($"{type.Name}.{method.Name}: live profile must use a discovery-time profile attribute");
            else if (fact.AttributeType == typeof(ProfileFactAttribute) || fact.AttributeType == typeof(ProfileTheoryAttribute))
            {
                if (fact.ConstructorArguments[0].Value is not string profile || !profiles.Contains(profile))
                    failures.Add($"{type.Name}.{method.Name}: attribute and Profile trait disagree");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static List<string> ProfileTraits(IEnumerable<CustomAttributeData> attributes)
        => attributes.Where(static attribute => attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments.Count == 2 && attribute.ConstructorArguments[0].Value is "Profile")
            .Select(static attribute => (string)attribute.ConstructorArguments[1].Value!).Distinct(StringComparer.Ordinal).ToList();
}
