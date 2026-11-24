using WpfStudio.App.Features.Packages;

namespace WpfStudio.Shell.Tests;

public sealed class NuGetPackageServiceCaseTests
{
    [Fact]
    public async Task ReadProjectReferencesResolvesMsBuildPropertiesCaseInsensitively()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpfstudio-nuget-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var projectPath = Path.Combine(directory, "CaseTest.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <MyPackageVersion>1.0.0</MyPackageVersion>
                    <mypackageversion>2.3.4</mypackageversion>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Example.Package" Version="$(MYPACKAGEVERSION)" />
                  </ItemGroup>
                </Project>
                """);

            var packages = await NuGetPackageService.ReadProjectReferencesAsync(projectPath, centralPath: null);
            var package = Assert.Single(packages);
            Assert.Equal("Example.Package", package.Id);
            Assert.Equal("2.3.4", package.RequestedVersion);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
